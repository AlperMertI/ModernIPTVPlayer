using Microsoft.UI.Xaml;
using Windows.UI;
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
using System.Net.Http;
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
using ModernIPTVPlayer.Helpers;
using Microsoft.UI.Xaml.Media;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Streaming;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ModernIPTVPlayer
{
    public record PlayerNavigationArgs(string Url, string Title, string Id = null, string ParentId = null, string SeriesName = null, int Season = 0, int Episode = 0, double StartSeconds = -1, string PosterUrl = null, string Type = null, string BackdropUrl = null, string LogoUrl = null, string PrimaryColor = null, string SourceAddonUrl = null)
    {
        public DateTime LaunchTimestampUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed partial class PlayerPage : Page
    {

        private MpvPlayer? _mpvPlayer;
        private Windows.Media.Playback.MediaPlayer? _nativeMediaPlayer;
        private Windows.Media.Streaming.Adaptive.AdaptiveMediaSource? _adaptiveMediaSource;
        private ulong _downloadSpeedBytes;
        private DateTime _downloadSpeedWindowStart = DateTime.MinValue;
        private ulong _downloadSpeedWindowBytes;
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
        private DateTime _sessionStartTime;
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
        private bool _isPaused = false;

        // ---------- SUBTITLE & SYNC STATE ----------
        private bool _isAudioDelayMode = false; // true = Audio, false = Subtitle
        private double _audioDelayMs = 0;
        private double _subDelayMs = 0;
        private List<SubtitleLanguageViewModel> _subtitleLanguages = new();
        private List<SubtitleTrackViewModel> _currentSubtitleTracks = new();
        private List<SubtitleTrackViewModel> _cachedAddonSubtitles = new(); // Cache for fetched addon subs
        private string _lastAddonFetchId = null; // Track which ID we cached for
        private bool _isFetchingAddonSubs = false;

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
        private string _cachedDisplayHdrStatus = "-";
        private string _cachedDisplayHdrAvailable = "-";
        private string _cachedDisplaySdrWhite = "-";
        private string _cachedDisplayLuminance = "-";
        private int _nativeMonitorFps = 0;
        private bool _isHandoff = false;
        private bool _bufferUnlocked = false;
        private DateTime _lastFullScreenToggle = DateTime.MinValue;
        private const bool NativeHdrExperimentDisableRealTimePlayback = false;
        private const bool NativePlaybackVerboseDiagnostics = true;
        private string? _currentNativePlaybackUrl;
        private Stopwatch? _startupStopwatch;
        private DateTime _launchTimestampUtc = DateTime.MinValue;
        private int _nativeTeardownState = 0;
        private int _nativePlaybackGeneration = 0;
        private long _nativeSessionId = 0;
        private long _activeNativePlayerSessionId = 0;
        private readonly object _nativeTeardownLock = new();
        private Task _nativeTeardownTask = Task.CompletedTask;
        
        // Auto-Hide Logic
        private DispatcherTimer? _cursorTimer;
        private bool _controlsHidden = false;
        private bool _isCursorHidden = false;
        private bool _isInfoPillsAnimating = false;
        private DateTimeOffset _lastInfoPillsShowTime = DateTimeOffset.MinValue;
        private bool _isPiPMode = false;
        private bool _isPiPTransitionRunning = false;
        private bool _pipOverlayAnimationsInitialized = false;
        private bool _pipControlsVisible = false;
        private bool _isPiPDragging = false;
        private Windows.Graphics.PointInt32 _pipDragLastPosition;
        private bool _suppressPointerReveal = false;
        private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

        
        private bool _isMpvStatsVisible = false;
        
        // [PiP] Single Window State Preservation
        private Windows.Graphics.RectInt32 _savedWindowBounds;
        private bool _savedIsFullScreen;
        private OverlappedPresenterState _savedPresenterState;
        private AppWindowPresenterKind _savedPresenterKind;

        private DispatcherTimer _logoLoadingTimer;
        private double _fakeLogoProgress = 0;

        // ---------- PLAYER ENHANCEMENTS ----------
        private DispatcherTimer _inactivityTimer;
        private DateTime _pauseStartTime;
        private bool _isInactivityOverlayVisible = false;
        private Color _primaryColor = Color.FromArgb(255, 0, 191, 165);
        private bool _isNextEpisodeOverlayVisible = false;
        private DispatcherTimer _nextEpCountdownTimer;
        private int _nextEpCountdown = 10;
        private string? _sourceAddonUrl;
        private string? _primaryColorHex;
        private EpisodeItem _nextEpisode;

        private DispatcherTimer _resizeDebounceTimer;
        private bool _isResizingWindow = false;

        private struct SourceColorMetadata
        {
            public uint? Primaries;
            public uint? Transfer;
            public uint? Matrix;
            public uint? Range;
            public string Source;
        }

        public PlayerPage()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

            // UI Audio Feedback Setup
            this.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Off;
            BackButton.ElementSoundMode = global::Microsoft.UI.Xaml.ElementSoundMode.Default;

            this.Loaded += PlayerPage_Loaded;
            
            // Silent codec check on background for logs
            _ = Helpers.CodecHelper.GetCodecSupportStatusAsync();
            // Hide default back button since we have a custom one and pane is closed
            // But navigation service back requests still need handling in MainWindow

            // Resize optimizations
            _resizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _resizeDebounceTimer.Tick += ResizeDebounceTimer_Tick;
            this.SizeChanged += PlayerPage_SizeChanged;

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

            // Inactivity Timer
            _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _inactivityTimer.Tick += InactivityTimer_Tick;

            // MainGrid Tap for Play/Pause - Use AddHandler to catch even if handled by children
            MainGrid.AddHandler(TappedEvent, new TappedEventHandler(MainGrid_Tapped), true);

            // [PROPER FIX] Start the Local Proxy Service for Native Playback stability
            Services.StreamProxyService.Instance.Start();
        }

        private double _loadingTargetProgress = 0; // The goal % the animation seeks to
        private void StartLogoLoading()
        {
            if (_navArgs != null && !string.IsNullOrWhiteSpace(_navArgs.LogoUrl))
            {
                try
                {
                    var uri = new Uri(_navArgs.LogoUrl);
                    SilhoutteLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
                    ColoredLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
                    PlayerLoadingOverlay.Visibility = Visibility.Visible;
                    LogoProgressClip.Rect = new Windows.Foundation.Rect(0, 0, 0, 120);

                    _fakeLogoProgress = 0;
                    // Phase 1: Quick setup only (URL resolve + player init — ~100ms with optimizations)
                    // Kept low so the bar doesn't look "stuck" during the fast network connect phase
                    _loadingTargetProgress = 30;

                    if (_logoLoadingTimer == null)
                    {
                        _logoLoadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                        _logoLoadingTimer.Tick += (s, ev) =>
                        {
                            if (_fakeLogoProgress < _loadingTargetProgress)
                            {
                                // Smooth easing up to target
                                double distance = _loadingTargetProgress - _fakeLogoProgress;
                                _fakeLogoProgress += Math.Max(0.1, distance * 0.02);

                                if (_fakeLogoProgress > _loadingTargetProgress) _fakeLogoProgress = _loadingTargetProgress;

                                LogoProgressClip.Rect = new Windows.Foundation.Rect(0, 0, (_fakeLogoProgress / 100.0) * 300, 120);
                            }
                        };
                    }
                    _logoLoadingTimer.Start();
                } catch { } // URI parsing might fail
            }
        }

        private void StopLogoLoading()
        {
            if (PlayerLoadingOverlay.Visibility == Visibility.Visible)
            {
                // Playback has definitively started, so we instantly snap to 100% 
                // and skip waiting for the smooth animation to visually catch up.
                _loadingTargetProgress = 100;
                _fakeLogoProgress = 100;
                _logoLoadingTimer?.Stop();
                
                LogoProgressClip.Rect = new Windows.Foundation.Rect(0, 0, 300, 120);
                
                // Fade out very quickly so it doesn't block playback
                var fadeOut = ElementCompositionPreview.GetElementVisual(this).Compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f);
                fadeOut.Duration = TimeSpan.FromMilliseconds(200);
                fadeOut.DelayTime = TimeSpan.FromMilliseconds(0); 
                
                var visual = ElementCompositionPreview.GetElementVisual(PlayerLoadingOverlay);
                visual.StartAnimation("Opacity", fadeOut);
                
                var t = Task.Run(async () => 
                {
                    await Task.Delay(250);
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        PlayerLoadingOverlay.Visibility = Visibility.Collapsed;
                        visual.Opacity = 1f; // Reset for next time
                    });
                });
            }
        }

        private void ShowBufferingOverlay()
        {
            if (_isInactivityOverlayVisible) return;

            if (PlayerLoadingOverlay.Visibility != Visibility.Visible && _isPageLoaded)
            {
                // Reset visual state without the initial fade-in delay
                var visual = ElementCompositionPreview.GetElementVisual(PlayerLoadingOverlay);
                visual.Opacity = 1f;

                // Start from 30% — the stream is already resolved, buffering will drive the rest
                _fakeLogoProgress = 30;
                _loadingTargetProgress = 30;
                LogoProgressClip.Rect = new Windows.Foundation.Rect(0, 0, (_fakeLogoProgress / 100.0) * 300, 120);
                
                PlayerLoadingOverlay.Visibility = Visibility.Visible;
                
                // Restart visual smoother if necessary, though mostly driven by StatsTimer_Tick now
                if (_logoLoadingTimer != null && !_logoLoadingTimer.IsEnabled)
                {
                    _logoLoadingTimer.Start();
                }
            }
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
            if (!IsPlayerActive) return;

            try 
            {
                // [LEAK_HUNT] Memory tracking every 10 seconds (Improved labels)
                if (DateTime.Now.Second % 10 == 0)
                {
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    long managedHeap = GC.GetTotalMemory(false);
                    long cumulativeAlloc = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                    Debug.WriteLine($"[MEM_DEBUG] WS={proc.WorkingSet64/1024/1024}MB | Private={proc.PrivateMemorySize64/1024/1024}MB | Heap={managedHeap/1024/1024}MB | Cumulative_Alloc={cumulativeAlloc/1024/1024}MB");

                    // Periodic telemetry that isn't event-based or high frequency (container specs)
                    try {
                        if (_mpvPlayer != null)
                        {
                            var mpvFps = await _mpvPlayer.GetPropertyAsync("container-fps");
                            var mpvResW = await _mpvPlayer.GetPropertyAsync("video-params/res-w");
                            var mpvResH = await _mpvPlayer.GetPropertyAsync("video-params/res-h");
                            var mpvCodec = await _mpvPlayer.GetPropertyAsync("video-codec");

                            if (!string.IsNullOrEmpty(mpvFps) && double.TryParse(mpvFps, NumberStyles.Any, CultureInfo.InvariantCulture, out double fpsVal))
                                TxtFps.Text = fpsVal.ToString("F2");

                            if (!string.IsNullOrEmpty(mpvResW) && !string.IsNullOrEmpty(mpvResH)) 
                                TxtResolution.Text = $"{mpvResW}x{mpvResH}";

                            if (!string.IsNullOrEmpty(mpvCodec) && !TxtHardware.Text.Contains($"({mpvCodec})")) 
                                TxtHardware.Text += $" ({mpvCodec})";
                        }
                    } catch { }
                }

                // ---------- SEEKBAR & TIME LOGIC & LOGO SYNC ----------
                // These require lower-latency polling for smooth UI updates
                string durationStr = "0", positionStr = "0", coreIdleStr = "no", pausedForCacheStr = "no", seekingStr = "no";
                double duration = 0, position = 0;

                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    // [UI_PERF] Skip polling intensive properties until media is fully loaded and demuxed.
                    // This prevents the underlying library from throwing "Property Unavailable" exceptions
                    // which fill the debug console during startup.
                    if (_mpvPlayer.IsMediaLoaded)
                    {
                        durationStr = await _mpvPlayer.GetPropertyAsync("duration");
                        positionStr = await _mpvPlayer.GetPropertyAsync("time-pos");
                        coreIdleStr = await _mpvPlayer.GetPropertyAsync("core-idle");
                        pausedForCacheStr = await _mpvPlayer.GetPropertyAsync("paused-for-cache");
                        seekingStr = await _mpvPlayer.GetPropertyAsync("seeking");
                        
                        double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out duration);
                        double.TryParse(positionStr, NumberStyles.Any, CultureInfo.InvariantCulture, out position);
                    }
                    else
                    {
                        // Default values during loading
                        coreIdleStr = "yes";
                        pausedForCacheStr = "yes";
                    }
                }
                else if (!_useMpvPlayer && _nativeMediaPlayer?.PlaybackSession != null)
                {
                    var sess = _nativeMediaPlayer.PlaybackSession;
                    duration = sess.NaturalDuration.TotalSeconds;
                    position = sess.Position.TotalSeconds;
                    coreIdleStr = sess.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing ? "yes" : "no";
                    pausedForCacheStr = sess.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Buffering ? "yes" : "no";
                    // Native player uses a built in buffering UI but we sync it
                }

                // --- LOGO LOADING SYNC LOGIC ---
                if (PlayerLoadingOverlay.Visibility == Visibility.Visible)
                {
                    bool isTimeAdvancing = false;
                    
                    if (_useMpvPlayer)
                    {
                        bool isCoreIdle = coreIdleStr == "yes";
                        bool isBuffering = pausedForCacheStr == "yes" || seekingStr == "yes" || position < 0.1;
                        isTimeAdvancing = position > 0.05 && !isCoreIdle && !isBuffering;
                    }
                    else if (_nativeMediaPlayer?.PlaybackSession != null)
                    {
                        // Native player might not advance Position immediately for some live streams
                        isTimeAdvancing = _nativeMediaPlayer.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
                    }
                    
                    if (isTimeAdvancing)
                    {
                        // Playback officially started
                        StopLogoLoading();
                    }
                    else if (_loadingTargetProgress != 100)
                    {
                        if (_useMpvPlayer && _mpvPlayer != null && _mpvPlayer.IsMediaLoaded)
                        {
                            // Phase 2: MPV opened, reading cache fill status (0-100)
                            string bufferingStateStr = await _mpvPlayer.GetPropertyAsync("cache-buffering-state");

                            if (seekingStr == "yes")
                            {
                                // Reset visually during seek
                                _loadingTargetProgress = 30;
                                _fakeLogoProgress = 30;
                            }
                            else if (double.TryParse(bufferingStateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double bufferPercentage))
                            {
                                // Map MPV's 0-100% buffer fill to our UI's target range (30-100%)
                                double newTarget = 30.0 + (bufferPercentage * 0.70);

                                // During a seek or cache drop, occasionally the buffer % genuinely falls.
                                // Set target to exactly what MPV reports. _logoLoadingTimer handles smoothness.
                                _loadingTargetProgress = newTarget;
                            }
                            else
                            {
                                 // If buffering state isn't available but we are loading, nudge it slightly
                                 if (_loadingTargetProgress < 90) _loadingTargetProgress += 1;
                            }
                        }
                        else if (!_useMpvPlayer && _nativeMediaPlayer?.PlaybackSession != null)
                        {
                            if (seekingStr == "yes")
                            {
                                _loadingTargetProgress = 30;
                                _fakeLogoProgress = 30;
                            }
                            else
                            {
                                double bufferPercentage = _nativeMediaPlayer.PlaybackSession.BufferingProgress * 100.0; // 0.0 to 1.0 mapped to 0-100
                                _loadingTargetProgress = 30.0 + (bufferPercentage * 0.70);
                            }
                        }
                        else
                        {
                            if (_loadingTargetProgress < 90) _loadingTargetProgress += 1;
                        }
                    }
                }
                else
                {
                    // Mid-stream buffering detection (user sought, rewound, or connection dropped)
                    if (pausedForCacheStr == "yes" || seekingStr == "yes")
                    {
                         ShowBufferingOverlay();
                    }
                }

                // ---------- HANDOFF BUFFER UNLOCK LOGIC ----------
                // Ensuring buffer limits are lifted only after playback truly begins prevent startup race conditions.
                if (_isHandoff && !_bufferUnlocked && position > 0.1)
                {
                    System.Diagnostics.Debug.WriteLine($"[HANDOFF_UNLOCK] Playback started at {position}s. Applying main buffer settings...");
                    
                    // Apply main buffer settings from user preferences
                    bool isLive = _streamUrl != null && (_streamUrl.Contains("/live/") || _streamUrl.Contains(".m3u8") || _streamUrl.Contains(":8080") || _streamUrl.Contains("/ts"));
                    await MpvSetupHelper.ApplyBufferSettingsAsync(_mpvPlayer, false, isLive);
                    
                    _bufferUnlocked = true;
                }

                string seekable = "no";
                double cacheDuration = 0;

                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    seekable = await _mpvPlayer.GetPropertyAsync("seekable");
                    double.TryParse(await _mpvPlayer.GetPropertyAsync("demuxer-cache-duration"), NumberStyles.Any, CultureInfo.InvariantCulture, out cacheDuration);
                }
                else if (!_useMpvPlayer && _nativeMediaPlayer?.PlaybackSession != null)
                {
                    seekable = _nativeMediaPlayer.PlaybackSession.CanSeek ? "yes" : "no";
                    cacheDuration = 10.0; // Assume okay for native
                }

                bool isSeekable = _useMpvPlayer 
                    ? (seekable != "no" || cacheDuration > 3.0) 
                    : (_nativeMediaPlayer?.PlaybackSession != null && _nativeMediaPlayer.PlaybackSession.CanSeek);
                
                bool isLikelyLiveUrl = _streamUrl != null && IsLikelyLiveUrl(_streamUrl);

                // Native MF: Special handling for live streams that report fake CanSeek
                if (!_useMpvPlayer && _nativeMediaPlayer != null)
                {
                    var sess = _nativeMediaPlayer.PlaybackSession;
                    
                    if (isLikelyLiveUrl || (sess != null && sess.NaturalDuration > TimeSpan.FromHours(5)))
                    {
                        // Stricter check: Raw TS streams are almost never properly seekable in MF Live
                        if (_streamUrl != null && _streamUrl.Contains(".ts") && !_streamUrl.Contains(".m3u8"))
                        {
                            isSeekable = false;
                        }
                    }
                }

                var seekVisibility = isSeekable ? Visibility.Visible : Visibility.Collapsed;
                RewindButton.Visibility = seekVisibility;
                FastForwardButton.Visibility = seekVisibility;
                SeekSlider.Visibility = seekVisibility;
                
                RewindButton.IsEnabled = isSeekable;
                FastForwardButton.IsEnabled = isSeekable;
                SeekSlider.IsEnabled = isSeekable;
                RewindButton.Opacity = isSeekable ? 1.0 : 0.5;
                FastForwardButton.Opacity = isSeekable ? 1.0 : 0.5;

                // Removed redundant "Live Detection Logic" block (lines 87-102) to avoid flickering
                // and rely on the robust "isLikelyLive" check below.
                
                string fileFormat = "";
                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    fileFormat = await _mpvPlayer.GetPropertyAsync("file-format");
                }
                else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                {
                    // MF: Check actual playback session properties
                    var session = _nativeMediaPlayer.PlaybackSession;
                    if (session != null)
                    {
                        // Live streams often have very long or infinite duration
                        // MF may report CanSeek=true for live streams, so check duration
                        var naturalDuration = session.NaturalDuration;
                        if (naturalDuration > TimeSpan.FromHours(5))
                        {
                            // If duration > 5 hours, likely live or very long content
                            fileFormat = "live";
                        }
                        else if (_streamUrl != null && IsLikelyLiveUrl(_streamUrl))
                        {
                            // URL patterns strongly suggest live stream
                            fileFormat = "live";
                        }
                        else
                        {
                            // Additional MF live detection based on format
                            fileFormat = session.CanSeek ? "seekable" : "nonseekable";
                        }
                    }
                }
                
                // Final Live Heuristic check before Visibility
                bool isMpvLiveFormat = _useMpvPlayer && (fileFormat == "hls" || fileFormat == "apple-http" || fileFormat == "mpegts");
                // isLikelyLiveUrl is already defined above
                
                bool isLikelyLive = (fileFormat == "live") || isMpvLiveFormat || isLikelyLiveUrl || (!isSeekable);

                bool isMpegTs = (fileFormat == "mpegts" || (_streamUrl != null && _streamUrl.Contains(".ts")));

                // Track Progress (VOD & Live)
                if (_navArgs != null && position > 1)
                {
                    string id = !string.IsNullOrEmpty(_navArgs.Id) ? _navArgs.Id : _navArgs.Url;
                    
                    if (!isLikelyLive && duration > 0)
                    {
                        // VOD Progress
                        HistoryManager.Instance.UpdateProgress(id, _navArgs.Title, _navArgs.Url, position, duration, _navArgs.ParentId, _navArgs.SeriesName, _navArgs.Season, _navArgs.Episode, null, null, null, _navArgs.PosterUrl, _navArgs.Type, _navArgs.BackdropUrl);
                    }
                    else if (isLikelyLive)
                    {
                        // Live Progress (just timestamp and metadata)
                        HistoryManager.Instance.UpdateProgress(id, _navArgs.Title, _navArgs.Url, 0, 0, null, null, 0, 0, null, null, null, _navArgs.LogoUrl, "live");
                    }
                }

                if (isLikelyLive)
                {
                    // Update _isBehind state based on ABSOLUTE SIMPLICITY (user request)
                    // Rule: Opening is LIVE. Only switch to BEHIND on Pause, Manual Rewind, or Stalling (>5s).

                    if (_isPaused) 
                    {
                        _isBehind = true;
                    }
                    
                    // Buffering Detection (Donmuşsa/Takılmışsa)
                    // Only check for stalls after 10 seconds of playback to ignore initial buffering.
                    double sessionElapsed = (DateTime.Now - _sessionStartTime).TotalSeconds;
                    if (_isBehind == false && sessionElapsed > 10.0 && (DateTime.Now - _lastSeekTime).TotalSeconds > 5.0)
                    {
                        bool isStalled = false;
                        if (_useMpvPlayer && _mpvPlayer != null) {
                            try {
                               var idle = await _mpvPlayer.GetPropertyAsync("core-idle");
                               var buffering = await _mpvPlayer.GetPropertyAsync("paused-for-cache");
                               isStalled = (idle == "yes" || buffering == "yes");
                            } catch {}
                        } else if (_nativeMediaPlayer != null) {
                            isStalled = (_nativeMediaPlayer.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Buffering);
                        }

                        if (isStalled && !_isPaused) {
                             _isBehind = true;
                        }
                    }

                    // LIVE UI MODE
                    LiveButton.Visibility = Visibility.Visible;
                    TimeTextBlock.Visibility = Visibility.Collapsed;
                    
                    // User Request: In live mode, hide progress bar (slider) even if seekable.
                    SeekSlider.Visibility = Visibility.Collapsed;
                    
                    RewindButton.Visibility = isSeekable ? Visibility.Visible : Visibility.Collapsed;
                    FastForwardButton.Visibility = isSeekable ? Visibility.Visible : Visibility.Collapsed;

                    if (_isPaused || _isBehind)
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
                else
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
                    string format = tDur.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss";
                    TimeTextBlock.Text = $"{tPos.ToString(format)} / {tDur.ToString(format)}";

                    // Check for Next Episode (Series) or Recommendations (Movie) nearing end
                    CheckEndContentFlow(position, duration);

                    if (_isInactivityOverlayVisible)
                    {
                        UpdateInactivityRemainingTime();
                    }
                }

                // ---------- STATS UPDATES (Conditional & Cached) ----------
                // ---------- STATS UPDATES (Conditional & Cached) ----------
                
                // 1. Static Metadata (Fetched once automatically on start, then refreshed occasionally)
                int metadataRefreshTicks = 20; // Every 10 seconds (20 * 500ms)
                bool shouldRefreshMetadata = !_isStaticMetadataFetched || (DateTime.Now.Second % 10 == 0 && _isPageLoaded);

                if (shouldRefreshMetadata)
                {
                    UpdateDisplayAdvancedColorSnapshot();
                    ApplyDisplayInfoToUI();

                    if (_useMpvPlayer && _mpvPlayer != null && _mpvPlayer.IsMediaLoaded)
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

                                string mpvMatrix = await _mpvPlayer.GetPropertyAsync("video-params/colormatrix");
                                string mpvRange = await _mpvPlayer.GetPropertyAsync("video-params/colorlevels");
                                string mpvPrimaries = await _mpvPlayer.GetPropertyAsync("video-params/primaries");
                                string mpvTransfer = await _mpvPlayer.GetPropertyAsync("video-params/gamma");

                                _cachedColorspace = BuildMpvColorspaceSummary(mpvMatrix, mpvRange, mpvPrimaries);
                                _cachedHdr = BuildMpvHdrSummary(mpvPrimaries, mpvTransfer);

                                ApplyMetadataToUI(true);
                            }
                        }
                    }
                    else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                    {
                        var session = _nativeMediaPlayer.PlaybackSession;
                        var wSize = session.NaturalVideoWidth;
                        var hSize = session.NaturalVideoHeight;
                        
                        if (wSize > 0 && !_isStaticMetadataFetched)
                        {
                            string newRes = $"{wSize}x{hSize}";
                            
                            // Polling fallback (if event missed)
                            string newFps = "- fps";
                            string newCodec = "NATIVE";
                            string newAudio = "NATIVE";
                            string newHdr = "SDR/HDR (Native)";

                            try 
                            {
                                if (_nativeMediaPlayer.Source is Windows.Media.Playback.MediaPlaybackItem item)
                                {
                                    if (item.VideoTracks.Count > 0)
                                    {
                                        var videoProps = item.VideoTracks[0].GetEncodingProperties();
                                        if (videoProps.FrameRate.Denominator > 0)
                                        {
                                            double fv = (double)videoProps.FrameRate.Numerator / videoProps.FrameRate.Denominator;
                                            newFps = $"{fv:F2} fps";
                                        }
                                        newCodec = GetShortCodecName(videoProps.Subtype);
                                        UpdateNativeSourceColorStats(videoProps);
                                    }
                                    if (item.AudioTracks.Count > 0)
                                    {
                                        var audioProps = item.AudioTracks[0].GetEncodingProperties();
                                        newAudio = $"{GetShortCodecName(audioProps.Subtype).ToUpper()} ({audioProps.ChannelCount}ch)";
                                    }
                                }
                            } catch { }

                            _cachedResolution = newRes;
                            _cachedFps = newFps;
                            if (_nativeMonitorFps > 0) _cachedFps += $" / {_nativeMonitorFps}Hz";
                            
                            _cachedCodec = newCodec;
                            _cachedAudio = newAudio;
                            if (_cachedColorspace == "-" || string.IsNullOrWhiteSpace(_cachedColorspace))
                            {
                                _cachedColorspace = "Auto";
                            }
                            if (_cachedHdr == "-" || string.IsNullOrWhiteSpace(_cachedHdr))
                            {
                                _cachedHdr = newHdr;
                            }
                            
                            ApplyMetadataToUI(false);
                        }
                    }
                }

                if (StatsOverlay.Visibility == Visibility.Visible)
                {
                    // 2. Dynamic Metadata (Always poll when visible, but only if media is loaded)
                    if (_useMpvPlayer && _mpvPlayer != null && _mpvPlayer.IsMediaLoaded)
                    {
                        RowAppliedPeak.Visibility = Visibility.Visible;
                        RowSdrWhite.Visibility = Visibility.Visible;
                        RowSpeed.Visibility = Visibility.Visible;
                        RowBitrate.Visibility = Visibility.Visible;
                        RowAvSync.Visibility = Visibility.Visible;
                        RowDropped.Visibility = Visibility.Visible;

                        var bitrate = await _mpvPlayer.GetPropertyAsync("video-bitrate");
                        TxtBitrate.Text = FormatBitrate(bitrate);
                        
                        long speedVal = await GetPropertyLongSafe("cache-speed");
                        TxtSpeed.Text = FormatSpeedLong(speedVal);

                        var hwdec = await _mpvPlayer.GetPropertyAsync("hwdec-current");
                        var vo = await _mpvPlayer.GetPropertyAsync("vo-configured");
                        TxtHardware.Text = (hwdec != "no" && !string.IsNullOrEmpty(hwdec)) ? $"{hwdec.ToUpper()} ({vo})" : $"SOFTWARE ({vo})";
                        TxtRenderer.Text = _mpvPlayer.RenderApi == "d3d11" ? "GPU-NEXT (Placebo)" : "GPU (Legacy DXGI)";
                        TxtAppliedPeak.Text = _mpvPlayer.AppliedPeak > 0 ? $"{_mpvPlayer.AppliedPeak:F0} nits" : "-";
                        TxtSdrWhite.Text = _mpvPlayer.SdrWhiteLevel > 0 ? $"{_mpvPlayer.SdrWhiteLevel:F0} nits" : "-";

                        // Drops & AV Sync
                        try 
                        {
                            long decDrops = await GetPropertyLongSafe("frame-drop-count");
                            if (decDrops < 0) decDrops = await GetPropertyLongSafe("vo-drop-frame-count");
                            if (decDrops < 0) decDrops = await GetPropertyLongSafe("decoder-frame-drop-count");
                            
                            string avSync = await _mpvPlayer.GetPropertyAsync("avsync");
                            string buffDur = await _mpvPlayer.GetPropertyAsync("demuxer-cache-duration");

                            TxtDroppedDecoder.Text = decDrops >= 0 ? $"{decDrops}" : "0";

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
                    else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                    {
                        var session = _nativeMediaPlayer.PlaybackSession;
                        
                        RowAppliedPeak.Visibility = Visibility.Collapsed;
                        RowSdrWhite.Visibility = Visibility.Collapsed;
                        RowAvSync.Visibility = Visibility.Collapsed;
                        RowDropped.Visibility = Visibility.Collapsed;

                        if (_adaptiveMediaSource != null)
                        {
                            ulong speedBytes;
                            lock (this) { speedBytes = _downloadSpeedBytes; }
                            if (speedBytes > 0)
                            {
                                double mbps = (speedBytes * 8.0) / 1000000.0;
                                TxtSpeed.Text = $"{mbps:F1} Mbps";
                            }
                            else
                            {
                                TxtSpeed.Text = "-";
                            }
                            RowSpeed.Visibility = Visibility.Visible;

                            ulong downloadBitrate = _adaptiveMediaSource.CurrentDownloadBitrate;
                            if (downloadBitrate > 0)
                            {
                                double brMbps = downloadBitrate / 1000000.0;
                                TxtBitrate.Text = $"{brMbps:F1} Mbps";
                            }
                            else
                            {
                                TxtBitrate.Text = "-";
                            }
                            RowBitrate.Visibility = Visibility.Visible;

                            TxtHardware.Text = "HARDWARE";
                        }
                        else
                        {
                            RowSpeed.Visibility = Visibility.Collapsed;
                            RowBitrate.Visibility = Visibility.Collapsed;
                            TxtHardware.Text = "HARDWARE";
                        }

                        TxtRenderer.Text = "GPU (DirectComposition/Native)";
                        
                        double buffPct = session.BufferingProgress * 100.0;
                        TxtBuffer.Text = buffPct > 0 ? $"{buffPct:F1}%" : "0%";
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

            // Don't show info pills if controls are hidden - they'll be shown
            // automatically when controls become visible
            if (_controlsHidden) return;

            PillResolution.Text = _cachedResolution;
            PillFps.Text = _cachedFps;
            PillCodec.Text = _cachedCodec;

            if (InfoPillsStack.Visibility != Visibility.Visible)
            {
                 InfoPillsStack.Visibility = Visibility.Visible;
                 ShowInfoPillsAnim.Begin();
            }
        }

        private async void ApplyMetadataToUI(bool isMpv)
        {
            // Update UI
            TxtResolution.Text = _cachedResolution;
            TxtFps.Text = _cachedFps;
            TxtCodec.Text = _cachedCodec.ToUpper();
            TxtAudioCodec.Text = _cachedAudio;
            TxtColorspace.Text = _cachedColorspace;
            TxtHdr.Text = _cachedHdr;
            ApplyDisplayInfoToUI();
            
            PillResolution.Text = _cachedResolution;
            PillFps.Text = _cachedFps;
            PillCodec.Text = _cachedCodec;

            _isStaticMetadataFetched = true;
            ShowInfoPills();

            // [CACHE UPDATE] Update global cache with real playback data
            try 
            {
                bool isHdr = IsHdrContentLabel(_cachedHdr);
                string simpleFps = _cachedFps.Split(' ')[0] + " fps";
                
                long bitrate = 0;
                if (isMpv && _mpvPlayer != null)
                {
                    string brStr = await _mpvPlayer.GetPropertyAsync("video-bitrate");
                    if (double.TryParse(brStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double brVal)) 
                        bitrate = (long)brVal;
                }

                if (_navArgs != null && int.TryParse(_navArgs.Id, out int streamId))
                {
                    Services.ProbeCacheService.Instance.Update(streamId, _cachedResolution, simpleFps, _cachedCodec, bitrate, isHdr);
                    Debug.WriteLine($"[PlayerPage] Metadata Updated in Global Cache for ID {streamId} ({_cachedResolution})");
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[PlayerPage] Global Cache Update Failed: {ex.Message}"); }
        }

        protected override async void OnKeyDown(KeyRoutedEventArgs e)
        {
            base.OnKeyDown(e);
            
            if (e.Key == Windows.System.VirtualKey.Space)
            {
                TogglePlayPause();
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.S)
            {
                // Toggle Custom Stats Overlay
                StatsOverlay.Visibility = StatsOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                e.Handled = true;
                return;
            }

            // The following keybindings are strictly MPV commands. We disable them for Native Player for now.
            if (!_useMpvPlayer || _mpvPlayer == null) return;

            if (e.Key == Windows.System.VirtualKey.Left)
            {
                // Seek Backward
                int seekAmt = AppSettings.SeekBackwardSeconds;
                ShowOsd($"-{seekAmt} SN");
                await _mpvPlayer.ExecuteCommandAsync("seek", $"-{seekAmt}", "relative");
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Right)
            {
                // Seek Forward
                int seekAmt = AppSettings.SeekForwardSeconds;
                ShowOsd($"+{seekAmt} SN");
                await _mpvPlayer.ExecuteCommandAsync("seek", seekAmt.ToString(), "relative");
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
            else if (e.Key == Windows.System.VirtualKey.Space)
            {
                TogglePlayPause();
                e.Handled = true;
            }
        }

        private async void TogglePlayPause()
        {
            if (_useMpvPlayer)
            {
                if (_mpvPlayer == null) return;
                bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                await _mpvPlayer.SetPropertyAsync("pause", isPaused ? "no" : "yes");
            }
            else if (_nativeMediaPlayer != null)
            {
                if (_nativeMediaPlayer.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                    _nativeMediaPlayer.Pause();
                else
                    _nativeMediaPlayer.Play();
                
                bool isNowPaused = _nativeMediaPlayer.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing;
                PlayPauseIcon.Glyph = isNowPaused ? "\uF8AE" : "\uF5B0";
                if (PipPlayPauseIcon != null) PipPlayPauseIcon.Glyph = isNowPaused ? "\uF8AE" : "\uF5B0";
            }
            
            // Logic is now handled reactively in StatsTimer_Tick
            
            // Ensure focus is kept on MainGrid for keyboard shortcuts
            MainGrid.Focus(FocusState.Programmatic);
        }

        private void MainGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Only toggle if we're not touching controls or overlays
            if (e.OriginalSource is FrameworkElement fe)
            {
                // List of element names that should NOT trigger play/pause when tapped
                string[] excludedNames = { "SeekSlider", "VolumeSlider", "SpeedOverlay", "TracksOverlay", "BackButton", "PlayPauseButton" };
                
                if (excludedNames.Contains(fe.Name)) return;

                // Also check if we are inside an interactive overlay or container
                var parent = fe;
                while (parent != null && parent != MainGrid)
                {
                    if (parent is Button || parent is Slider) return;
                    if (parent.Name == "ControlsBorder") return; // Don't toggle when clicking control bar area
                    parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }

                TogglePlayPause();
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {

            base.OnNavigatedTo(e);
            _startupStopwatch = Stopwatch.StartNew();

            if (e.Parameter is string url)
            {
                _streamUrl = url;
                VideoTitleText.Text = ""; // No title provided
                _navArgs = new PlayerNavigationArgs(url, "");
                _launchTimestampUtc = _navArgs.LaunchTimestampUtc;
            }
            else if (e.Parameter is PlayerNavigationArgs args)
            {
                _streamUrl = args.Url;
                VideoTitleText.Text = args.Title;
                _navArgs = args;
                _launchTimestampUtc = args.LaunchTimestampUtc;

                if (!string.IsNullOrEmpty(args.PrimaryColor))
                {
                    try { _primaryColor = AppColorHelper.ToColor(args.PrimaryColor); } catch { }
                    _primaryColorHex = args.PrimaryColor;
                }
                _sourceAddonUrl = args.SourceAddonUrl;
            }
            else
            {
                _navigationError = "Geçersiz yayın URL'si alındı.";
            }

            _navigationError = null;

            // Initial UI Coloring
            ApplyPrimaryColorToUi();
            LogPlayerTrace($"[TIMING] NavigatedTo -> args captured at {_startupStopwatch.ElapsedMilliseconds}ms");
            LogLaunchTiming("NavigatedTo");
        }

        private void LogStartupTiming(string label)
        {
            if (_startupStopwatch == null)
            {
                return;
            }

            LogPlayerTrace($"[TIMING] {label} at {_startupStopwatch.ElapsedMilliseconds}ms");
        }

        private void LogLaunchTiming(string label)
        {
            if (_launchTimestampUtc == DateTime.MinValue)
            {
                return;
            }

            var elapsedMs = (DateTime.UtcNow - _launchTimestampUtc).TotalMilliseconds;
            LogPlayerTrace($"[TIMING] {label} since click: {elapsedMs:F0}ms");
        }

        private void LogPlayerTrace(string message)
        {
            Debug.WriteLine(message);
        }

        private void LogDiagnosticTrace(string message)
        {
            Debug.WriteLine(message);
        }

        private bool IsNativeTeardownRequested => System.Threading.Volatile.Read(ref _nativeTeardownState) != 0;

        private long CurrentNativeSessionId => System.Threading.Interlocked.Read(ref _nativeSessionId);

        private void ResetNativeTeardownState()
        {
            System.Threading.Interlocked.Exchange(ref _nativeTeardownState, 0);
        }

        private bool TryBeginNativeTeardown()
        {
            return System.Threading.Interlocked.Exchange(ref _nativeTeardownState, 1) == 0;
        }

        private int BumpNativePlaybackGeneration()
        {
            return System.Threading.Interlocked.Increment(ref _nativePlaybackGeneration);
        }

        private long BeginNativeSession()
        {
            return System.Threading.Interlocked.Increment(ref _nativeSessionId);
        }

        private bool IsCurrentNativeSession(long sessionId)
        {
            return sessionId != 0 && sessionId == CurrentNativeSessionId && !IsNativeTeardownRequested;
        }

        private sealed class NativeTeardownContext
        {
            public long SessionId { get; init; }
            public int PlaybackGeneration { get; init; }
            public Windows.Media.Playback.MediaPlayer? MainPlayer { get; init; }
            public Windows.Media.Streaming.Adaptive.AdaptiveMediaSource? AdaptiveSource { get; init; }
        }

        private void QueueNativeTeardown(NativeTeardownContext teardownContext, string reason)
        {
            if (teardownContext.MainPlayer == null &&
                teardownContext.AdaptiveSource == null)
            {
                return;
            }

            lock (_nativeTeardownLock)
            {
                _nativeTeardownTask = _nativeTeardownTask.ContinueWith(
                    _ => DisposeCapturedNativeStateAsync(teardownContext, reason),
                    TaskScheduler.Default).Unwrap();
            }
        }

        private async Task DisposeCapturedNativeStateAsync(NativeTeardownContext teardownContext, string reason)
        {
            await Task.Yield();

            try
            {
                LogPlayerTrace($"[PlayerPage] Native teardown queued for session={teardownContext.SessionId}, generation={teardownContext.PlaybackGeneration}, reason={reason}");

                if (teardownContext.AdaptiveSource != null)
                {
                    try
                    {
                        teardownContext.AdaptiveSource.DownloadCompleted -= AdaptiveSource_DownloadCompleted;
                    }
                    catch { }
                }

                if (teardownContext.MainPlayer != null)
                {
                    DisposeNativePlayerCore(teardownContext.MainPlayer, "[PlayerPage] Native media player");
                }
            }
            catch (Exception ex)
            {
                LogPlayerTrace($"[PlayerPage] Native teardown error for session={teardownContext.SessionId}: {ex.Message}");
            }
        }

        private void DisposeNativePlayerCore(Windows.Media.Playback.MediaPlayer player, string label)
        {
            try
            {
                LogPlayerTrace($"{label} dispose started");
                try { player.Pause(); } catch { }
                try { player.Source = null; } catch { }
                try { player.Dispose(); } catch { }
                LogPlayerTrace($"{label} dispose completed");
            }
            catch (Exception ex)
            {
                LogPlayerTrace($"{label} dispose error: {ex.Message}");
            }
        }

        private NativeTeardownContext DetachNativeResources(bool collapseNativeSurface)
        {
            var teardownContext = new NativeTeardownContext
            {
                SessionId = CurrentNativeSessionId,
                PlaybackGeneration = _nativePlaybackGeneration,
                MainPlayer = _nativeMediaPlayer,
                AdaptiveSource = _adaptiveMediaSource
            };

            _currentNativePlaybackUrl = null;
            _adaptiveMediaSource = null;
            _nativeMediaPlayer = null;
            _activeNativePlayerSessionId = 0;

            if (teardownContext.AdaptiveSource != null)
            {
                try
                {
                    teardownContext.AdaptiveSource.DownloadCompleted -= AdaptiveSource_DownloadCompleted;
                }
                catch { }
            }

            if (teardownContext.MainPlayer != null)
            {
                try { teardownContext.MainPlayer.MediaOpened -= NativePlayer_MediaOpened; } catch { }
                try { teardownContext.MainPlayer.MediaFailed -= MediaPlayer_MediaFailed; } catch { }
                try { teardownContext.MainPlayer.PlaybackSession.PlaybackStateChanged -= NativePlaybackSession_PlaybackStateChanged; } catch { }
            }

            try
            {
                MediaFoundationPlayer.SetMediaPlayer(null);
            }
            catch { }

            try { MediaFoundationPlayer.Source = null; } catch { }

            if (collapseNativeSurface)
            {
                try
                {
                    MediaFoundationPlayer.Visibility = Visibility.Collapsed;
                    PlayerContainer.Visibility = Visibility.Visible;
                }
                catch { }
            }

            return teardownContext;
        }

        private bool SessionOwnsNativePlayer(Windows.Media.Playback.MediaPlayer sender)
        {
            return sender != null &&
                   sender == _nativeMediaPlayer &&
                   _activeNativePlayerSessionId != 0 &&
                   _activeNativePlayerSessionId == CurrentNativeSessionId &&
                   !IsNativeTeardownRequested;
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {

            // 1. Stop timer IMMEDIATELY
            _statsTimer?.Stop();
            _logoLoadingTimer?.Stop();
            StopCursorTimer();
            if (_isCursorHidden) { SetCursorVisible(true); }
            RemoveCursorHook();
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
            Services.SleepPreventionService.AllowSleep();
            _inactivityTimer?.Stop();
            _nextEpCountdownTimer?.Stop();

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
                    // Instead, we just PAUSE playback and return the player to the source page.
                    _ = _mpvPlayer.ExecuteCommandAsync("set", "pause", "yes");
                    App.HandoffPlayer = _mpvPlayer; // Return player to source page for reuse
                    
                    // We DO NOT call DisableHandoffMode(); -> PreserveStateOnUnload keeps RenderControl alive.
                    
                    Debug.WriteLine("[PlayerPage] Returned handed-off player to source page (Paused, Buffer Preserved).");
                }
                else
                {
                    try
                    {
                        // [UI_PERF] Offload CleanupAsync to background thread
                        // This allows navigation to proceed instantly without waiting for libmpv shutdown
                        var playerToCleanup = _mpvPlayer;
                        _mpvPlayer = null; // Nullify immediately to stop all UI-thread interactions
                        
                        _ = Task.Run(async () => 
                        {
                            try 
                            {
                                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] Background Cleanup STARTED");
                                await playerToCleanup.CleanupAsync();
                                playerToCleanup.PropertyChanged -= OnMpvPropertyChanged;
                                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] Background Cleanup COMPLETED");
                            }
                            catch (Exception ex) 
                            { 
                                Debug.WriteLine($"[LIFECYCLE] Background Cleanup Error: {ex.Message}"); 
                            }
                        });
                    }
                    catch (Exception ex) { Debug.WriteLine($"[LIFECYCLE] Cleanup Setup Error: {ex}"); }
                }
                _mpvPlayer = null;
            }
            else
            {
                BeginNativeTeardown(collapseNativeSurface: false, reason: "navigation");
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

            await Services.DialogService.ShowAsync(dialog);
        }

        private void ApplyPrimaryColorToUi()
        {
            try
            {
                var brush = new SolidColorBrush(_primaryColor);
                if (SeekSlider != null) SeekSlider.Foreground = brush;
                if (InactivityTitle != null) InactivityTitle.Foreground = brush;
            }
            catch { }
        }

        private void InactivityTimer_Tick(object sender, object e)
        {
            if ((_useMpvPlayer && _mpvPlayer == null) || (!_useMpvPlayer && _nativeMediaPlayer == null)) return;

            var idleTime = (DateTime.Now - _pauseStartTime).TotalSeconds;
            if (idleTime >= 30 && !_isInactivityOverlayVisible)
            {
                ShowInactivityOverlay();
            }
        }
        private async void ShowInactivityOverlay()
        {
            if (InactivityOverlay == null) return;
            
            _isInactivityOverlayVisible = true;
            InactivityOverlay.Visibility = Visibility.Visible;
            
            // Apply blur immediately before any potential await delays (e.g. metadata fetching)
            if (_useMpvPlayer && _mpvPlayer != null)
            {
                _ = _mpvPlayer.ExecuteCommandAsync("vf", "add", "@inact:gblur=sigma=15");
            }
            
            // Hide buffering overlay if it's visible so it doesn't overlap the panel
            if (PlayerLoadingOverlay.Visibility == Visibility.Visible)
            {
                PlayerLoadingOverlay.Visibility = Visibility.Collapsed;
            }
            
            // 1. Populate metadata from args (initial)
            if (_navArgs != null)
            {
                InactivityTitle.Text = _navArgs.Type == "series" ? (_navArgs.SeriesName ?? _navArgs.Title) : _navArgs.Title;
                InactivityDescription.Text = _navArgs.Title; 
                
                if (!string.IsNullOrEmpty(_navArgs.LogoUrl))
                {
                    InactivityLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_navArgs.LogoUrl));
                    InactivityLogo.Visibility = Visibility.Visible;
                    InactivityTitle.Visibility = Visibility.Collapsed;
                }
                else
                {
                    InactivityLogo.Visibility = Visibility.Collapsed;
                    InactivityTitle.Visibility = Visibility.Visible;
                }

                if (_navArgs.Type == "movie")
                {
                    double duration = 0;
                    if (_useMpvPlayer && _mpvPlayer != null)
                    {
                        string durationStr = await _mpvPlayer.GetPropertyAsync("duration");
                        double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out duration);
                    }
                    else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                    {
                        duration = _nativeMediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
                    }
                    
                    TimeSpan tDur = TimeSpan.FromSeconds(duration);
                    InactivityDuration.Text = tDur.TotalHours >= 1 ? $"{tDur.Hours}s {tDur.Minutes}dk" : $"{tDur.Minutes}dk";
                    InactivityChapterInfo.Visibility = Visibility.Collapsed;
                }
                else
                {
                    InactivityDuration.Text = $"S{_navArgs.Season} E{_navArgs.Episode}";
                    InactivityChapterInfo.Text = _navArgs.Title;
                    InactivityChapterInfo.Visibility = Visibility.Visible;
                }

                // 2. Fetch full metadata for enhanced details (Consistency with MediaInfoPage)
                try 
                {
                    var metaId = _navArgs.Type == "series" ? _navArgs.ParentId : _navArgs.Id;
                    var unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(
                        new Models.Stremio.StremioMediaStream(new StremioMeta { Id = metaId, Type = _navArgs.Type }));

                    if (unified != null) 
                    {
                        if (_navArgs.Type == "series")
                        {
                            var ep = unified.Seasons?
                                .FirstOrDefault(s => s.SeasonNumber == _navArgs.Season)?
                                .Episodes.FirstOrDefault(e => e.EpisodeNumber == _navArgs.Episode);
                            
                            InactivityDescription.Text = !string.IsNullOrEmpty(ep?.Overview) ? ep.Overview : unified.Overview;
                        }
                        else
                        {
                            InactivityDescription.Text = unified.Overview;
                        }
                        
                        InactivityYear.Text = unified.Year?.Split('-', '–')[0] ?? "";
                        InactivityRating.Text = unified.Rating > 0 ? unified.Rating.ToString("F1", CultureInfo.InvariantCulture) : "";
                        InactivityRatingBorder.Visibility = unified.Rating > 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                } 
                catch {}

                PopulateTechnicalBadges();
                UpdateInactivityRemainingTime();
            }

            AnimateOpacity(InactivityOverlay, 1.0, 0.5);
        }

        private void PopulateTechnicalBadges()
        {
            if (InactivityBadges == null) return;
            InactivityBadges.Children.Clear();

            // 1. HDR / SDR
            bool isHdr = IsHdrContentLabel(TxtHdr.Text);
            if (isHdr)
                AddBadge("HDR", "SilverGradient", isGradient: true);
            else
                AddBadge("SDR", "#66FFFFFF", isGradient: false);

            // 2. Resolution (4K / 1080P / etc)
            string res = TxtResolution.Text;
            bool is4K = res.Contains("3840") || res.Contains("4K") || res.Contains("2160");
            if (is4K)
                AddBadge("4K UHD", "GoldGradient", isGradient: true);
            else if (!string.IsNullOrWhiteSpace(res) && res != "-")
            {
                string displayRes = res;
                if (displayRes.Contains("x")) displayRes = displayRes.Split('x').LastOrDefault() + "P";
                AddBadge(displayRes.ToUpperInvariant(), "#66FFFFFF", isGradient: false);
            }

            // 3. Codec
            if (!string.IsNullOrWhiteSpace(TxtCodec.Text) && TxtCodec.Text != "-")
            {
                AddBadge(TxtCodec.Text.ToUpperInvariant(), "#66FFFFFF", isGradient: false);
            }
        }

        private void AddBadge(string text, string colorKeyOrHex, bool isGradient)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Background = new SolidColorBrush(Color.FromArgb(68, 0, 0, 0)) // #44000000
            };

            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            };

            if (isGradient)
            {
                border.BorderBrush = (Brush)Resources[colorKeyOrHex];
                textBlock.Foreground = (Brush)Resources[colorKeyOrHex];
            }
            else
            {
                var brush = new SolidColorBrush(AppColorHelper.ToWindowsColor(colorKeyOrHex));
                border.BorderBrush = brush;
                textBlock.Foreground = brush;
                border.BorderThickness = new Thickness(1);
            }

            border.Child = textBlock;
            InactivityBadges.Children.Add(border);
        }

        private async void UpdateInactivityRemainingTime()
        {
            if (!_isInactivityOverlayVisible) return;
            
            double pos = 0;
            double dur = 0;
            
            if (_useMpvPlayer && _mpvPlayer != null)
            {
                string posStr = await _mpvPlayer.GetPropertyAsync("time-pos");
                string durStr = await _mpvPlayer.GetPropertyAsync("duration");
                
                double.TryParse(posStr, NumberStyles.Any, CultureInfo.InvariantCulture, out pos);
                double.TryParse(durStr, NumberStyles.Any, CultureInfo.InvariantCulture, out dur);
            }
            else if (!_useMpvPlayer && _nativeMediaPlayer != null)
            {
                pos = _nativeMediaPlayer.PlaybackSession.Position.TotalSeconds;
                dur = _nativeMediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
            }
            
            if (dur > 0)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(dur - pos);
                string remainingStr = "";
                
                if (remaining.TotalHours >= 1)
                {
                    remainingStr = $"{(int)remaining.TotalHours}sa {remaining.Minutes}dk kaldı";
                }
                else if (remaining.TotalMinutes >= 1)
                {
                    remainingStr = $"{remaining.Minutes}dk kaldı";
                }
                else
                {
                    remainingStr = $"{remaining.Seconds}sn kaldı";
                }

                InactivityRemainingTime.Text = remainingStr;
            }
        }

        private void HideInactivityOverlay()
        {
            if (InactivityOverlay == null || !_isInactivityOverlayVisible) return;
            
            _isInactivityOverlayVisible = false;
            
            if (_useMpvPlayer && _mpvPlayer != null)
            {
                _ = _mpvPlayer.ExecuteCommandAsync("vf", "remove", "@inact");
            }

            AnimateOpacity(InactivityOverlay, 0.0, 0.3);

            _ = Task.Run(async () => {
                try
                {
                    await Task.Delay(350);
                    DispatcherQueue.TryEnqueue(() => {
                        if (!_isInactivityOverlayVisible) InactivityOverlay.Visibility = Visibility.Collapsed;
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlayerPage] Overlay hide error: {ex.Message}");
                }
            });
        }

        private void AnimateOpacity(UIElement element, double to, double durationSeconds)
        {
            if (!AreUiAnimationsEnabled() || durationSeconds <= 0)
            {
                var snapVisual = ElementCompositionPreview.GetElementVisual(element);
                snapVisual.Opacity = (float)to;
                return;
            }

            var anim = _compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, (float)to);
            anim.Duration = TimeSpan.FromSeconds(durationSeconds);
            
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StartAnimation("Opacity", anim);
        }

        private void NextEpPlayNowBtn_Click(object sender, RoutedEventArgs e)
        {
            PlayNextEpisode();
        }

        private void NextEpSelectSourceBtn_Click(object sender, RoutedEventArgs e)
        {
            _nextEpCountdownTimer?.Stop();

            // Mark current as finished so it's cleared from continue watching
            if (_navArgs != null)
            {
                string id = !string.IsNullOrEmpty(_navArgs.Id) ? _navArgs.Id : _navArgs.Url;
                HistoryManager.Instance.UpdateProgress(id, null, null, 0, 0, forceFinished: true);
                _ = HistoryManager.Instance.SaveAsync();
            }

            // Navigate back with special intent: "Select source for next episode"
            if (_nextEpisode != null)
            {
                var dict = new Dictionary<string, object>
                {
                    { "Intent", "SelectSource" },
                    { "Season", _nextEpisode.SeasonNumber },
                    { "Episode", _nextEpisode.EpisodeNumber }
                };
                // We use a shared state or find a way to pass it back. 
                // Since Frame.GoBack() doesn't take parameters, we'll use a static property in MediaInfoPage or App.
                App.LastPlayerIntent = dict;
            }
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void PlayNextEpisode()
        {
             _nextEpCountdownTimer?.Stop();
             if (_nextEpisode == null) return;

             // Mark current as finished before navigating to next
             if (_navArgs != null)
             {
                 string currentId = !string.IsNullOrEmpty(_navArgs.Id) ? _navArgs.Id : _navArgs.Url;
                 HistoryManager.Instance.UpdateProgress(currentId, null, null, 0, 0, forceFinished: true);
                 _ = HistoryManager.Instance.SaveAsync();
             }

             try 
             {
                 // 1. Search for stream for next episode
                 string type = "series";
                 string id = $"{_navArgs.ParentId}:{_nextEpisode.SeasonNumber}:{_nextEpisode.EpisodeNumber}";
                 var addonUrls = Services.Stremio.StremioAddonManager.Instance.GetAddonsByResource("stream");
                 var streams = await Services.Stremio.StremioService.Instance.GetStreamsAsync(addonUrls, type, id);
                 
                 if (streams == null || streams.Count == 0)
                 {
                     if (Frame.CanGoBack) Frame.GoBack();
                     return;
                 }

                 // [REFINEMENT] Pick best stream: Prioritize the SAME ADDON that was used for the current episode
                 var bestStream = streams.FirstOrDefault(s => s.AddonUrl == _sourceAddonUrl) 
                                 ?? streams.FirstOrDefault();

                 var nextArgs = new PlayerNavigationArgs(
                     bestStream.Url,
                     _nextEpisode.Title,
                     id,
                     _navArgs.ParentId,
                     _navArgs.SeriesName ?? _navArgs.Title,
                     _nextEpisode.SeasonNumber,
                     _nextEpisode.EpisodeNumber,
                     0, // StartSeconds
                     _navArgs.PosterUrl,
                     "series",
                     _navArgs.BackdropUrl,
                     _navArgs.LogoUrl,
                     _primaryColorHex,
                     _sourceAddonUrl
                 );

                 // Navigate to new PlayerPage
                 Frame.Navigate(typeof(PlayerPage), nextArgs);
             }
             catch (Exception ex)
             {
                 Debug.WriteLine($"[PlayerPage] PlayNextEpisode Error: {ex}");
                 if (Frame.CanGoBack) Frame.GoBack();
             }
        }

        private bool _isNextEpLoading = false;
        private void CheckEndContentFlow(double position, double duration)
        {
            if (duration <= 0 || _isNextEpLoading) return;

            double remaining = duration - position;
            
            // Show Next Episode overlay 2 minutes before end for series
            if (_navArgs?.Type == "series" && remaining < 120 && remaining > 5 && !_isNextEpisodeOverlayVisible)
            {
                _isNextEpLoading = true;
                _ = LoadNextEpisodeAsync();
            }
            // Show Recommendations 2 minutes before end for movies
            else if (_navArgs?.Type == "movie" && remaining < 120 && remaining > 5 && !RecommendationsOverlay.Visibility.HasFlag(Visibility.Visible))
            {
                 // _ = LoadRecommendationsAsync();
            }
        }

        private async Task LoadNextEpisodeAsync()
        {
            try
            {
                if (_navArgs == null || _navArgs.Type != "series") return;

                // 1. Fetch Series Metadata to find next episode
                var unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(new Models.Stremio.StremioMediaStream(new StremioMeta { Id = _navArgs.ParentId, Type = "series" }));
                if (unified == null) return;

                var currentSeason = unified.Seasons.FirstOrDefault(s => s.SeasonNumber == _navArgs.Season);
                if (currentSeason == null) return;

                var nextEp = currentSeason.Episodes
                    .OrderBy(e => e.EpisodeNumber)
                    .FirstOrDefault(e => e.EpisodeNumber > _navArgs.Episode);

                if (nextEp == null)
                {
                    // Look in next season
                    var nextSeason = unified.Seasons.OrderBy(s => s.SeasonNumber).FirstOrDefault(s => s.SeasonNumber > _navArgs.Season);
                    if (nextSeason != null)
                    {
                        nextEp = nextSeason.Episodes.OrderBy(e => e.EpisodeNumber).FirstOrDefault();
                    }
                }

                if (nextEp != null)
                {
                    _nextEpisode = new EpisodeItem
                    {
                        Id = nextEp.Id,
                        Title = nextEp.Title,
                        SeasonNumber = nextEp.SeasonNumber,
                        EpisodeNumber = nextEp.EpisodeNumber,
                        Overview = nextEp.Overview,
                        ImageUrl = nextEp.ThumbnailUrl
                    };
                    
                    DispatcherQueue.TryEnqueue(() => {
                        NextEpTitle.Text = nextEp.Title;
                        NextEpInfo.Text = $"S{nextEp.SeasonNumber} E{nextEp.EpisodeNumber}";
                        if (!string.IsNullOrEmpty(nextEp.ThumbnailUrl))
                            NextEpThumbnail.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(nextEp.ThumbnailUrl));
                        
                        NextEpisodeOverlay.Visibility = Visibility.Visible;
                        _isNextEpisodeOverlayVisible = true;
                        AnimateOpacity(NextEpisodeOverlay, 1.0, 0.5);
                        StartNextEpCountdown();
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlayerPage] LoadNextEpisode Error: {ex.Message}");
            }
            finally
            {
                _isNextEpLoading = false;
            }
        }

        private async Task LoadRecommendationsAsync()
        {
             try
             {
                 if (_navArgs == null || string.IsNullOrEmpty(_navArgs.Id)) return;
                 // Extract numeric TMDB ID if possible
                 string tmdbId = _navArgs.Id;
                 if (tmdbId.StartsWith("tmdb:")) tmdbId = tmdbId.Substring(5);
                 
                 if (!int.TryParse(tmdbId, out _)) return;

                 var recs = await TmdbHelper.GetMovieRecommendationsAsync(tmdbId);
                 if (recs != null && recs.Count > 0)
                 {
                     DispatcherQueue.TryEnqueue(() => {
                         RecommendationsListView.ItemsSource = recs;
                         RecommendationsOverlay.Visibility = Visibility.Visible;
                         AnimateOpacity(RecommendationsOverlay, 1.0, 0.5);
                     });
                 }
             }
             catch { }
        }

        private void RecommendationItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TmdbMovieResult movie)
            {
                // Navigate to MediaInfoPage for the recommended movie
                var stream = new Models.Stremio.StremioMediaStream(new StremioMeta { Id = movie.Id.ToString(), Type = "movie", Name = movie.Title });
                Frame.Navigate(typeof(MediaInfoPage), stream);
            }
        }

        private void StartNextEpCountdown()
        {
            _nextEpCountdown = 10;
            if (_nextEpCountdownTimer == null)
            {
                _nextEpCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _nextEpCountdownTimer.Tick += (s, e) => {
                    _nextEpCountdown--;
                    NextEpCountdownText.Text = $"Oynat ({_nextEpCountdown})";
                    if (_nextEpCountdown <= 0)
                    {
                        _nextEpCountdownTimer.Stop();
                        PlayNextEpisode();
                    }
                };
            }
            _nextEpCountdownTimer.Start();
        }

        // Helper to extract ALL cookies from a CookieContainer (ignoring Domain restrictions)
        // MOVED TO MpvSetupHelper

        private async void PlayerPage_Loaded(object sender, RoutedEventArgs e)
        {

            if (_isPageLoaded) return;
            
            _isPageLoaded = true;
            ResetNativeTeardownState();
            LogLaunchTiming("PlayerPage Loaded");

            // Ensure keyboard shortcuts work immediately
            _ = Task.Run(async () => {
                try
                {
                    await Task.Delay(200);
                    DispatcherQueue.TryEnqueue(() => {
                        if (MainGrid != null) MainGrid.Focus(FocusState.Programmatic);
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlayerPage] Focus error: {ex.Message}");
                }
            });
            _isStaticMetadataFetched = false;
            _cachedResolution = "-";
            _cachedFps = "-";
            _cachedCodec = "-";
            _cachedColorspace = "-";
            _cachedHdr = "-";
            _cachedDisplayHdrStatus = "-";
            _cachedDisplayHdrAvailable = "-";
            _cachedDisplaySdrWhite = "-";
            _cachedDisplayLuminance = "-";

            StartCursorTimer();

            if (!string.IsNullOrEmpty(_navigationError))
            {
                await ShowMessageDialog("Hata", _navigationError);
                _navigationError = null;
                return;
            }

            StartLogoLoading();

            // 0. Lightweight URL resolve (iptv:// protocol → real URL, from cache only)
            _streamUrl = ResolveStreamUrl(_streamUrl);
            LogPlayerTrace($"[PlayerPage] Resolved stream URL: {_streamUrl}");
            LogStartupTiming("Resolve complete");

            // 0b. Fire-and-forget validation — does NOT block playback.
            //     The proxy/MediaFoundation will catch errors during open; this is just
            //     an early-warning system that logs diagnostics and pre-warms the connection.
            _ = ValidateStreamUrlInBackground(_streamUrl);

            _useMpvPlayer = AppSettings.PlayerSettings.Engine == Models.PlayerEngine.Mpv;

            if (_useMpvPlayer)
            {
                LogStartupTiming("MPV branch start");
                LogLaunchTiming("MPV branch start");
                MediaFoundationPlayer.Visibility = Visibility.Collapsed;
                MediaFoundationPlayer.Source = null;
                try { MediaFoundationPlayer.MediaPlayer?.Pause(); } catch {}

                if (App.HandoffPlayer != null)
                {
                    // HANDOFF MODE: FAST PATH (No Delay!)
                    LogPlayerTrace("[PlayerPage] Doing Handoff...");
                    LogStartupTiming("MPV handoff path");
                    LogLaunchTiming("MPV handoff path");
                    var pSettings = AppSettings.PlayerSettings;
                    _isHandoff = true;
                    _bufferUnlocked = false;
                    _mpvPlayer = App.HandoffPlayer;
                    App.HandoffPlayer = null; 
                    _sessionStartTime = DateTime.Now;

                    // Phase 2: RE-ENABLE VISUALS & UNLOCK BUFFER
                    try 
                    {
                        // 1. Restore Video Rendering
                        await _mpvPlayer.SetPropertyAsync("vid", "1");

                        // 2. Expand Buffers for active watch
                        int mainBuffer = AppSettings.BufferSeconds;
                        await _mpvPlayer.SetPropertyAsync("cache", "yes");
                        await _mpvPlayer.SetPropertyAsync("demuxer-readahead-secs", mainBuffer.ToString());
                        await _mpvPlayer.SetPropertyAsync("demuxer-max-bytes", "512MiB");
                        await _mpvPlayer.SetPropertyAsync("demuxer-max-back-bytes", "32MiB");

                        // 3. Trigger Phase 2: Visual Enhancements
                        await MpvSetupHelper.ApplyVisualSettingsAsync(_mpvPlayer);

                        // 4. Initial Color Space Sync
                        await _mpvPlayer.SyncHdrStatusAsync();
                    } catch { }

                    PlayerContainer.Children.Add(_mpvPlayer);
                    _mpvPlayer.Visibility = Visibility.Visible;
                    _mpvPlayer.Opacity = 1;
                    _mpvPlayer.IsHitTestVisible = false;
                    _mpvPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                    _mpvPlayer.VerticalAlignment = VerticalAlignment.Stretch;

                    _mpvPlayer.Redraw();

                    ApplyPrimaryColorToUi();
                    Services.SleepPreventionService.PreventSleep();

                    // 1. Initial State Restoration & UI Activation
                    _statsTimer?.Start(); 
                    await _mpvPlayer.SetPropertyAsync("pause", "no");
                    await _mpvPlayer.SetPropertyAsync("mute", "no");

                    // 2. Verification
                    var pIdle = await _mpvPlayer.GetPropertyAsync("core-idle");
                    var pPath = await _mpvPlayer.GetPropertyAsync("path");

                    if (string.IsNullOrEmpty(pPath) || pPath == "N/A")
                    {
                        if (_navArgs != null && _navArgs.StartSeconds > 0)
                        {
                            await _mpvPlayer.SetPropertyAsync("start", _navArgs.StartSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        LogStartupTiming("MPV OpenAsync start");
                        LogLaunchTiming("MPV OpenAsync start");
                        await _mpvPlayer.OpenAsync(_navArgs.Url);
                        LogStartupTiming("MPV OpenAsync done");
                        LogLaunchTiming("MPV OpenAsync done");
                        await _mpvPlayer.SetPropertyAsync("pause", "no");
                    }
                    else if (pIdle == "yes")
                    {
                        await _mpvPlayer.SetPropertyAsync("pause", "no");
                        await Task.Delay(200);
                        await _mpvPlayer.SetPropertyAsync("pause", "no");
                        _mpvPlayer.Redraw();
                    }

                    // [RESTORE SAVED AUDIO/SUBTITLE TRACKS]
                    string contentId = !string.IsNullOrEmpty(_navArgs?.Id) ? _navArgs.Id : _streamUrl;
                    var history = HistoryManager.Instance.GetProgress(contentId);
                    bool isHistoryStale = history != null && pSettings.PreferredLanguagesUpdatedAt > history.Timestamp;

                    if (history != null && !isHistoryStale)
                    {
                        if (!string.IsNullOrEmpty(history.AudioTrackId))
                            await _mpvPlayer.SetPropertyAsync("aid", history.AudioTrackId);
                        if (!string.IsNullOrEmpty(history.SubtitleTrackId))
                            await _mpvPlayer.SetPropertyAsync("sid", history.SubtitleTrackId);
                    }

                    _ = AutoFetchAndRestoreAddonSubtitleAsync();
                }
                else
                {
                     // FRESH START MODE
                     LogPlayerTrace("[PlayerPage] Starting Fresh Playback...");
                     LogStartupTiming("MPV fresh start");
                     LogLaunchTiming("MPV fresh start");
                     _sessionStartTime = DateTime.Now;
                     Services.Streaming.StreamSlotSimulator.Instance.StopAll();
                     await Task.Delay(50); 

                     _mpvPlayer = new MpvWinUI.MpvPlayer();

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
                         LogPlayerTrace($"[PlayerPage] Selected Render API: {_mpvPlayer.RenderApi}");
                     }
                     catch (Exception ex)
                     {
                         LogPlayerTrace($"[PlayerPage] Failed to load settings for RenderApi: {ex.Message}");
                          _mpvPlayer.RenderApi = "d3d11"; 
                     }

                     PlayerContainer.Children.Add(_mpvPlayer);
                     _mpvPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                     _mpvPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                     _mpvPlayer.IsHitTestVisible = false;
                     _mpvPlayer.PropertyChanged += OnMpvPropertyChanged;

                     try
                    {

                        if (_loadingTargetProgress < 70) _loadingTargetProgress = 70; 

                        if (_mpvPlayer == null) return;

                        await MpvSetupHelper.ConfigurePlayerAsync(_mpvPlayer, _streamUrl, isSecondary: false);
                        
                        // Use centralized buffer settings instead of hardcoded 2GB
                        bool isLive = _streamUrl != null && (_streamUrl.Contains("/live/") || _streamUrl.Contains(".m3u8") || _streamUrl.Contains(":8080") || _streamUrl.Contains("/ts"));
                        await MpvSetupHelper.ApplyBufferSettingsAsync(_mpvPlayer, false, isLive);
                        
                        if (_mpvPlayer == null) return; 

                        if (_navArgs != null && _navArgs.StartSeconds > 0)
                        {
                            await _mpvPlayer.SetPropertyAsync("start", _navArgs.StartSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }

                        await _mpvPlayer.OpenAsync(_streamUrl);

                        ApplyPrimaryColorToUi();
                        Services.SleepPreventionService.PreventSleep();

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
                        
                        // [RESTORE SAVED AUDIO/SUBTITLE TRACKS]
                         string contentId = !string.IsNullOrEmpty(_navArgs?.Id) ? _navArgs.Id : _streamUrl;
                         var history = HistoryManager.Instance.GetProgress(contentId);
                         var pSettings = AppSettings.PlayerSettings;

                         bool isHistoryStale = history != null && pSettings.PreferredLanguagesUpdatedAt > history.Timestamp;

                         if (history != null && !isHistoryStale)
                         {
                             if (!string.IsNullOrEmpty(history.AudioTrackId))
                                 await _mpvPlayer.SetPropertyAsync("aid", history.AudioTrackId);
                             if (!string.IsNullOrEmpty(history.SubtitleTrackId))
                                 await _mpvPlayer.SetPropertyAsync("sid", history.SubtitleTrackId);
                         }

                        _ = AutoFetchAndRestoreAddonSubtitleAsync();

                    }
                    catch (Exception ex)
                    {
                        LogPlayerTrace($"[PlayerPage] MPV Error: {ex}");
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
                // NATIVE MEDIA FOUNDATION PIPELINE
                _ = StartNativePlaybackAsync(_streamUrl);
            }
        }

        private async Task StartNativePlaybackAsync(string url)
        {
            LogPlayerTrace("[PlayerPage] Starting Native Media Foundation Pipeline...");
            int nativeGeneration = BumpNativePlaybackGeneration();
            long nativeSessionId = BeginNativeSession();

            LogStartupTiming("Native branch start");
            LogLaunchTiming("Native branch start");
            
            _isBehind = false;
                  _sessionStartTime = DateTime.Now; // Post-seek immunity
            _isPaused = false;
            _sessionStartTime = DateTime.Now;

            // Prevent MPV from running in the background and throwing "render_context not being called" errors
            if (App.HandoffPlayer != null)
            {
                try 
                { 
                    _ = App.HandoffPlayer.SetPropertyAsync("pause", "yes");
                    _ = App.HandoffPlayer.SetPropertyAsync("vid", "no");
                    _ = App.HandoffPlayer.SetPropertyAsync("aid", "no");
                } catch {}
                
                App.HandoffPlayer = null;
            }

            MediaFoundationPlayer.Visibility = Visibility.Visible;
            PlayerContainer.Visibility = Visibility.Collapsed;
            MediaFoundationPlayer.AreTransportControlsEnabled = false; // We use our own UI
            
            _nativeMediaPlayer = new Windows.Media.Playback.MediaPlayer();
            _activeNativePlayerSessionId = nativeSessionId;
            // Advanced Optimizations
            _nativeMediaPlayer.RealTimePlayback = !NativeHdrExperimentDisableRealTimePlayback;
            _nativeMediaPlayer.SystemMediaTransportControls.IsEnabled = true; // SMTC Kernel Priority
            // MediaFailed -> Fallback to MPV
            _nativeMediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
            _nativeMediaPlayer.MediaOpened += NativePlayer_MediaOpened;
            _nativeMediaPlayer.PlaybackSession.PlaybackStateChanged += NativePlaybackSession_PlaybackStateChanged;
            MediaFoundationPlayer.SetMediaPlayer(_nativeMediaPlayer);

            try
            {
                if (_adaptiveMediaSource != null)
                {
                    _adaptiveMediaSource.DownloadCompleted -= AdaptiveSource_DownloadCompleted;
                    _adaptiveMediaSource = null;
                }

                _currentNativePlaybackUrl = null;
                if (_nativeMediaPlayer.Source != null)
                {
                    try { _nativeMediaPlayer.Pause(); } catch { }
                    _nativeMediaPlayer.Source = null;
                }
                LogStartupTiming("Native source reset");
                LogLaunchTiming("Native source reset");

                // 1. Create URI safely
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                {
                    LogPlayerTrace("[PlayerPage] Native Media: Malformed URI. Triggering fallback.");
                    TriggerMpvFallback();
                    return;
                }

                // 2. Setup Headers for Native (via WinRT HttpClient)
                var filter = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
                filter.CacheControl.ReadBehavior = Windows.Web.Http.Filters.HttpCacheReadBehavior.MostRecent;
                var winrtClient = new Windows.Web.Http.HttpClient(filter);
                HttpHelper.ApplyDefaultHeaders(winrtClient);

                // 3. Choose the right native source path.
                // AdaptiveMediaSource is for manifests (.m3u8/.mpd), not raw live MPEG-TS.
                bool looksAdaptiveManifest =
                    uri.AbsoluteUri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsoluteUri.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsoluteUri.Contains(".ism", StringComparison.OrdinalIgnoreCase);

                if (looksAdaptiveManifest)
                {
                    LogStartupTiming("Native manifest path");
                    LogLaunchTiming("Native manifest path");
                    var adaptiveResult = await Windows.Media.Streaming.Adaptive.AdaptiveMediaSource.CreateFromUriAsync(uri, winrtClient);

                    if (!IsCurrentNativeSession(nativeSessionId) || _nativeMediaPlayer == null)
                    {
                        return;
                    }

                    if (adaptiveResult.Status == Windows.Media.Streaming.Adaptive.AdaptiveMediaSourceCreationStatus.Success)
                    {
                        _adaptiveMediaSource = adaptiveResult.MediaSource;
                        _adaptiveMediaSource.DownloadCompleted += AdaptiveSource_DownloadCompleted;
                        var source = Windows.Media.Core.MediaSource.CreateFromAdaptiveMediaSource(_adaptiveMediaSource);
                        var item = new Windows.Media.Playback.MediaPlaybackItem(source);
                        _currentNativePlaybackUrl = uri.ToString();
                        _nativeMediaPlayer.Source = item;
                        LogStartupTiming("Native manifest source assigned");
                        LogLaunchTiming("Native manifest source assigned");
                    }
                    else
                    {
                        LogPlayerTrace($"[PlayerPage] AdaptiveMediaSource failed ({adaptiveResult.Status}); falling back to Header-Aware Local Proxy...");
                        string proxyUrl = Services.StreamProxyService.Instance.GetProxyUrl(uri.ToString());
                        var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(proxyUrl));
                        var item = new Windows.Media.Playback.MediaPlaybackItem(source);
                        _currentNativePlaybackUrl = proxyUrl;
                        _nativeMediaPlayer.Source = item;
                        LogStartupTiming("Native proxy source assigned");
                        LogLaunchTiming("Native proxy source assigned");
                    }
                }
                else
                {
                    LogPlayerTrace("[PlayerPage] Direct live/TS URL detected; using Header-Aware Local Proxy immediately.");
                    LogStartupTiming("Native proxy path");
                    LogLaunchTiming("Native proxy path");
                    string proxyUrl = Services.StreamProxyService.Instance.GetProxyUrl(uri.ToString());
                    var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(proxyUrl));
                    var item = new Windows.Media.Playback.MediaPlaybackItem(source);
                    _currentNativePlaybackUrl = proxyUrl;
                    _nativeMediaPlayer.Source = item;
                    LogStartupTiming("Native proxy source assigned");
                    LogLaunchTiming("Native proxy source assigned");
                }

                // 4. Start Playback with "MediaOpened" timeout logic
                if (!IsCurrentNativeSession(nativeSessionId) || _nativeMediaPlayer == null)
                {
                    return;
                }

                LogStartupTiming("Native Play start");
                LogLaunchTiming("Native Play start");
                _nativeMediaPlayer.Play();
                LogStartupTiming("Native Play called");
                LogLaunchTiming("Native Play called");

                // Optional: Monitor for initialization hang
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!IsCurrentNativeSession(nativeSessionId) || nativeGeneration != _nativePlaybackGeneration)
                            {
                                return;
                            }

                            if (!_useMpvPlayer && _nativeMediaPlayer != null &&
                                (_nativeMediaPlayer.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Opening ||
                                 _nativeMediaPlayer.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.None))
                            {
                                LogPlayerTrace("[PlayerPage] Native Media: Initialization Timeout. Falling back to MPV.");
                                TriggerMpvFallback();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PlayerPage] Native init monitor error: {ex.Message}");
                    }
                });
                
                _statsTimer?.Start();
                SetupProfessionalAnimations();
                ApplyPrimaryColorToUi();
                Services.SleepPreventionService.PreventSleep();

                // Optional: Setup timed metadata for subtitles if Addons provide `.srt`
                _ = AutoFetchAndRestoreAddonSubtitleAsync();
            }
            catch (Exception ex)
            {
                LogPlayerTrace($"[PlayerPage] Native Media Error: {ex.Message}");
                TriggerMpvFallback();
            }
        }


        private void MediaPlayer_MediaFailed(Windows.Media.Playback.MediaPlayer sender, Windows.Media.Playback.MediaPlayerFailedEventArgs args)
        {
            if (!SessionOwnsNativePlayer(sender))
            {
                return;
            }

            var msg = args.ErrorMessage;
            var errorType = args.Error.ToString();
            LogPlayerTrace($"[PlayerPage] Native Media Failed: {msg} (Type: {errorType}). Falling back to MPV.");
            
            DispatcherQueue.TryEnqueue(async () => 
            {
                if (!SessionOwnsNativePlayer(sender))
                {
                    return;
                }

                // If it's a content-level error, check for missing codecs before falling back
                var codecStatus = await Helpers.CodecHelper.GetCodecSupportStatusAsync();
                string codecSummary = string.Join(", ", codecStatus.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
                LogPlayerTrace($"[PlayerPage] Codec Support at Failure: {codecSummary}");

                if (args.Error == Windows.Media.Playback.MediaPlayerError.SourceNotSupported)
                {
                    await CheckAndGuideForCodecsAsync();

                    if (!SessionOwnsNativePlayer(sender))
                    {
                        return;
                    }
                }

                TriggerMpvFallback();
            });
        }

        private void NativePlaybackSession_PlaybackStateChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        {
            if (_activeNativePlayerSessionId == 0 || _activeNativePlayerSessionId != CurrentNativeSessionId || IsNativeTeardownRequested)
            {
                return;
            }

            LogPlayerTrace($"[PlayerPage] Native State: {sender.PlaybackState}");

            // Update play/pause icon based on playback state
            DispatcherQueue.TryEnqueue(() =>
            {
                bool isNowPaused = (sender.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused);
                _isPaused = isNowPaused;
                PlayPauseIcon.Glyph = isNowPaused ? "\uF5B0" : "\uF8AE";
                if (PipPlayPauseIcon != null) PipPlayPauseIcon.Glyph = isNowPaused ? "\uF5B0" : "\uF8AE";
            });

            // Hide the loading overlay only when the first frame has actually been rendered.
            // MediaOpened means MF can read the stream, not that the first frame is composited.
            // We wait for position > 0.5s — by this point the D3D compositor has definitely
            // presented at least one frame, so the overlay fade reveals actual video, not black.
            if (sender.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing &&
                sender.Position.TotalSeconds > 0.5)
            {
                LogPlayerTrace("[PlayerPage] First frame composited — hiding loading overlay");
                DispatcherQueue.TryEnqueue(() => StopLogoLoading());
            }
        }

        private async Task CheckAndGuideForCodecsAsync()
        {
            var status = await Helpers.CodecHelper.GetCodecSupportStatusAsync();
            bool mpeg2Ok = status.ContainsKey("MPEG-2") && status["MPEG-2"];
            bool hevcOk = status.ContainsKey("HEVC") && status["HEVC"];

            if (!mpeg2Ok || !hevcOk)
            {
                string message = "Native oynatıcı için gerekli bazı codec bileşenleri sisteminizde eksik:\n\n";
                if (!mpeg2Ok) message += "• MPEG-2 Video Uzantısı (Standard TV kanalları için)\n";
                if (!hevcOk) message += "• HEVC Video Uzantısı (4K yayınlar için)\n";
                
                message += "\nBu bileşenleri Microsoft Store'dan yükleyerek daha akıcı bir deneyim alabilirsiniz.";

                ContentDialog dialog = new ContentDialog
                {
                    Title = "Codec Bilgisi",
                    Content = message,
                    PrimaryButtonText = "Store'u Aç",
                    CloseButtonText = "Atla",
                    XamlRoot = this.XamlRoot
                };

                var result = await Services.DialogService.ShowAsync(dialog);
                if (result == ContentDialogResult.Primary)
                {
                    string productId = !mpeg2Ok ? "9n9w87p66432" : "9n4wgh0z6vhq";
                    await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://pdp/?ProductId={productId}"));
                }
            }
        }

        private void NativePlayer_MediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            if (!SessionOwnsNativePlayer(sender))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!SessionOwnsNativePlayer(sender))
                {
                    return;
                }

                LogPlayerTrace("[PlayerPage] Native Media Opened. Waiting for first frame...");
                LogStartupTiming("Native MediaOpened");
                LogLaunchTiming("Native MediaOpened");
                // Don't hide overlay yet — MediaOpened means MF can read the stream,
                // not that the first frame is composited. StopLogoLoading() will be
                // called from PlaybackStateChanged when position actually advances.

                // Sync Static Metadata for Native
                try
                {
                    var session = sender.PlaybackSession;
                    var wSize = session.NaturalVideoWidth;
                    var hSize = session.NaturalVideoHeight;

                    if (wSize > 0)
                    {
                        _cachedResolution = $"{wSize}x{hSize}";

                        if (sender.Source is Windows.Media.Playback.MediaPlaybackItem item)
                        {
                            if (item.VideoTracks.Count > 0)
                            {
                                var videoProps = item.VideoTracks[0].GetEncodingProperties();
                                if (videoProps.FrameRate.Denominator > 0)
                                {
                                    double fv = (double)videoProps.FrameRate.Numerator / videoProps.FrameRate.Denominator;
                                    _cachedFps = $"{fv:F2} fps";
                                    if (_nativeMonitorFps > 0) _cachedFps += $" / {_nativeMonitorFps}Hz";
                                }
                                _cachedCodec = GetShortCodecName(videoProps.Subtype);
                                ConfigureHdrOutput(item, videoProps);
                                LogMediaFoundationDiagnostics(item, videoProps);
                                LogDisplayAdvancedColorState();
                                LogAutoDetectedColorMetadata(item, videoProps);
                                UpdateNativeSourceColorStats(videoProps);
                            }

                            if (item.AudioTracks.Count > 0)
                            {
                                var audioProps = item.AudioTracks[0].GetEncodingProperties();
                                _cachedAudio = $"{GetShortCodecName(audioProps.Subtype).ToUpper()} ({audioProps.ChannelCount}ch)";
                            }
                        }

                        ApplyMetadataToUI(false);
                    }
                }
                catch (Exception ex)
                {
                    LogPlayerTrace($"[PlayerPage] Native Metadata Fetch Error: {ex.Message}");
                }
            });
        }

        private async void ConfigureHdrOutput(Windows.Media.Playback.MediaPlaybackItem item, Windows.Media.MediaProperties.VideoEncodingProperties videoProps)
        {
            try
            {
                // Detect HDR content from codec subtype
                bool isHevc = videoProps.Subtype.Contains("HEVC") || videoProps.Subtype.Contains("HVC1") || videoProps.Subtype.Contains("HEVC10");
                
                if (!isHevc)
                {
                    if (_cachedHdr == "-" || string.IsNullOrWhiteSpace(_cachedHdr))
                    {
                        _cachedHdr = "SDR / BT.709";
                    }
                    return;
                }

                // HDR detection deferred - MediaFoundation handles HDR output automatically
                // when display is in HDR mode. Just mark content type.
                if (_cachedHdr == "-" || string.IsNullOrWhiteSpace(_cachedHdr))
                {
                    _cachedHdr = "HEVC / Unspecified";
                }
                LogPlayerTrace($"[PlayerPage] HDR HEVC content detected - MediaFoundation will output HDR when display is in HDR mode");
            }
            catch (Exception ex)
            {
                LogPlayerTrace($"[PlayerPage] HDR Configuration Error: {ex.Message}");
                if (_cachedHdr == "-" || string.IsNullOrWhiteSpace(_cachedHdr))
                {
                    _cachedHdr = "HEVC / Unspecified";
                }
            }
        }

        private void BeginNativeTeardown(bool collapseNativeSurface, string reason)
        {
            if (!TryBeginNativeTeardown())
            {
                return;
            }

            BumpNativePlaybackGeneration();

            var teardownContext = DetachNativeResources(collapseNativeSurface);
            QueueNativeTeardown(teardownContext, reason);
        }


        private void LogMediaFoundationDiagnostics(Windows.Media.Playback.MediaPlaybackItem item, Windows.Media.MediaProperties.VideoEncodingProperties videoProps)
        {
            try
            {
                LogDiagnosticTrace("[MF-DIAG] ========== MediaFoundation Diagnostics ==========");

                // 1. Video Encoding Properties
                LogDiagnosticTrace($"[MF-DIAG] --- Video Encoding Properties ---");
                LogDiagnosticTrace($"[MF-DIAG] Subtype: {videoProps.Subtype}");
                LogDiagnosticTrace($"[MF-DIAG] Type: {videoProps.Type}");
                LogDiagnosticTrace($"[MF-DIAG] Width: {videoProps.Width}");
                LogDiagnosticTrace($"[MF-DIAG] Height: {videoProps.Height}");
                LogDiagnosticTrace($"[MF-DIAG] Bitrate: {videoProps.Bitrate}");
                LogDiagnosticTrace($"[MF-DIAG] FrameRate: {videoProps.FrameRate.Numerator}/{videoProps.FrameRate.Denominator}");
                LogDiagnosticTrace($"[MF-DIAG] ProfileId: {videoProps.ProfileId}");
                LogDiagnosticTrace($"[MF-DIAG] PixelAspectRatio: {videoProps.PixelAspectRatio.Numerator}:{videoProps.PixelAspectRatio.Denominator}");

                // 2. Properties Dictionary (contains color space, HDR metadata, etc.)
                var properties = videoProps.Properties;
                if (properties != null && properties.Count > 0)
                {
                    LogDiagnosticTrace($"[MF-DIAG] --- Extended Properties ({properties.Count} keys) ---");
                    foreach (var kvp in properties)
                    {
                        try
                        {
                            string keyName = kvp.Key.ToString();
                            string valueStr = kvp.Value?.ToString() ?? "null";
                            string valueType = kvp.Value?.GetType().Name ?? "null";
                            LogDiagnosticTrace($"[MF-DIAG]   Key: {{{keyName}}} = {valueStr} (Type: {valueType})");
                        }
                        catch (Exception ex)
                        {
                            LogDiagnosticTrace($"[MF-DIAG]   Key: (error reading) - {ex.Message}");
                        }
                    }
                }
                else
                {
                    LogDiagnosticTrace($"[MF-DIAG] --- Extended Properties: EMPTY (0 keys) ---");
                }

                // 3. MediaPlaybackSession properties
                var session = _nativeMediaPlayer?.PlaybackSession;
                if (session != null)
                {
                    LogDiagnosticTrace($"[MF-DIAG] --- Playback Session ---");
                    LogDiagnosticTrace($"[MF-DIAG] NaturalVideoSize: {session.NaturalVideoWidth}x{session.NaturalVideoHeight}");
                    LogDiagnosticTrace($"[MF-DIAG] IsProtected: {session.IsProtected}");
                    LogDiagnosticTrace($"[MF-DIAG] PlaybackState: {session.PlaybackState}");
                    LogDiagnosticTrace($"[MF-DIAG] NormalizedSourceRect: X={session.NormalizedSourceRect.X}, Y={session.NormalizedSourceRect.Y}, W={session.NormalizedSourceRect.Width}, H={session.NormalizedSourceRect.Height}");

                    try
                    {
                        var degradation = session.GetOutputDegradationPolicyState();
                        if (degradation != null)
                        {
                            LogDiagnosticTrace($"[MF-DIAG] --- Output Degradation Policy ---");
                            LogDiagnosticTrace($"[MF-DIAG] VideoConstrictionReason: {degradation.VideoConstrictionReason}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDiagnosticTrace($"[MF-DIAG] GetOutputDegradationPolicyState error: {ex.Message}");
                    }
                }

                // 4. Track counts
                LogDiagnosticTrace($"[MF-DIAG] --- Track Counts ---");
                LogDiagnosticTrace($"[MF-DIAG] VideoTracks: {item.VideoTracks.Count}");
                LogDiagnosticTrace($"[MF-DIAG] AudioTracks: {item.AudioTracks.Count}");
                LogDiagnosticTrace($"[MF-DIAG] TimedMetadataTracks: {item.TimedMetadataTracks.Count}");

                // 5. Audio properties (for comparison)
                if (item.AudioTracks.Count > 0)
                {
                    var audioProps = item.AudioTracks[0].GetEncodingProperties();
                    LogDiagnosticTrace($"[MF-DIAG] --- Audio Track 0 ---");
                    LogDiagnosticTrace($"[MF-DIAG] Subtype: {audioProps.Subtype}");
                    LogDiagnosticTrace($"[MF-DIAG] ChannelCount: {audioProps.ChannelCount}");
                    LogDiagnosticTrace($"[MF-DIAG] SampleRate: {audioProps.SampleRate}");
                    LogDiagnosticTrace($"[MF-DIAG] Bitrate: {audioProps.Bitrate}");
                }

                LogDiagnosticTrace("[MF-DIAG] =================================================");
            }
            catch (Exception ex)
            {
                LogDiagnosticTrace($"[MF-DIAG] Diagnostic dump error: {ex.Message}");
            }
        }

        private void LogDisplayAdvancedColorState()
        {
            try
            {
                UpdateDisplayAdvancedColorSnapshot(logDiagnostics: true);
            }
            catch (Exception ex)
            {
                LogDiagnosticTrace($"[DISPLAY-HDR] Advanced color probe error: {ex.Message}");
            }
        }

        private void ApplyDisplayInfoToUI()
        {
            TxtDisplayHdrStatus.Text = _cachedDisplayHdrStatus;
            TxtDisplayHdrAvailable.Text = _cachedDisplayHdrAvailable;
            TxtDisplaySdrWhite.Text = _cachedDisplaySdrWhite;
            TxtDisplayLuminance.Text = _cachedDisplayLuminance;
        }

        private void UpdateDisplayAdvancedColorSnapshot(bool logDiagnostics = false)
        {
            try
            {
                if (MainWindow.Current == null)
                {
                    _cachedDisplayHdrStatus = "-";
                    _cachedDisplayHdrAvailable = "-";
                    _cachedDisplaySdrWhite = "-";
                    _cachedDisplayLuminance = "-";

                    if (logDiagnostics)
                    {
                        LogDiagnosticTrace("[DISPLAY-HDR] MainWindow.Current is null; skipping advanced color probe.");
                    }
                    return;
                }

                var displayInfo = Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(MainWindow.Current.AppWindow.Id);
                var ac = displayInfo.GetAdvancedColorInfo();

                string kind = ac.CurrentAdvancedColorKind switch
                {
                    Microsoft.Graphics.Display.DisplayAdvancedColorKind.StandardDynamicRange => "SDR",
                    Microsoft.Graphics.Display.DisplayAdvancedColorKind.WideColorGamut => "WCG",
                    Microsoft.Graphics.Display.DisplayAdvancedColorKind.HighDynamicRange => "HDR",
                    _ => $"Other ({ac.CurrentAdvancedColorKind})"
                };

                bool hdrAvailable = ac.IsAdvancedColorKindAvailable(Microsoft.Graphics.Display.DisplayAdvancedColorKind.HighDynamicRange);
                bool hdr10MetadataSupported = ac.IsHdrMetadataFormatCurrentlySupported(Microsoft.Graphics.Display.DisplayHdrMetadataFormat.Hdr10);

                _cachedDisplayHdrStatus = kind;
                _cachedDisplayHdrAvailable = hdrAvailable ? "Yes" : "No";
                _cachedDisplaySdrWhite = $"{ac.SdrWhiteLevelInNits:F1} nits";
                _cachedDisplayLuminance = $"{ac.MinLuminanceInNits:F4} / {ac.MaxAverageFullFrameLuminanceInNits:F1} / {ac.MaxLuminanceInNits:F1} nits";

                if (logDiagnostics)
                {
                    LogDiagnosticTrace("[DISPLAY-HDR] ========== Display Advanced Color ==========");
                    LogDiagnosticTrace($"[DISPLAY-HDR] CurrentAdvancedColorKind: {kind}");
                    LogDiagnosticTrace($"[DISPLAY-HDR] HDR Available On Display: {hdrAvailable}");
                    LogDiagnosticTrace($"[DISPLAY-HDR] HDR10 Metadata Supported Now: {hdr10MetadataSupported}");
                    LogDiagnosticTrace($"[DISPLAY-HDR] SDR White Level: {ac.SdrWhiteLevelInNits:F1} nits");
                    LogDiagnosticTrace($"[DISPLAY-HDR] Min Luminance: {ac.MinLuminanceInNits:F4} nits");
                    LogDiagnosticTrace($"[DISPLAY-HDR] Max Full Frame Luminance: {ac.MaxAverageFullFrameLuminanceInNits:F1} nits");
                    LogDiagnosticTrace($"[DISPLAY-HDR] Max Luminance: {ac.MaxLuminanceInNits:F1} nits");
                    LogDiagnosticTrace($"[DISPLAY-HDR] UI Snapshot: HDR={_cachedDisplayHdrStatus}, Available={_cachedDisplayHdrAvailable}, Luminance={_cachedDisplayLuminance}");
                    LogDiagnosticTrace("[DISPLAY-HDR] ===========================================");
                }
            }
            catch (Exception ex)
            {
                if (logDiagnostics)
                {
                    LogDiagnosticTrace($"[DISPLAY-HDR] Advanced color probe error: {ex.Message}");
                }
            }
        }

        private void UpdateNativeSourceColorStats(Windows.Media.MediaProperties.VideoEncodingProperties videoProps)
        {
            var mfColor = ReadMfSourceColorMetadata(videoProps);
            var proxyColor = ReadStreamProxySourceColorMetadata();
            var effective = MergeSourceColorMetadata(mfColor, proxyColor);

            _cachedColorspace = BuildNativeColorspaceSummary(effective);
            _cachedHdr = BuildNativeHdrSummary(effective, videoProps.Subtype);

            if (string.IsNullOrWhiteSpace(_cachedColorspace))
            {
                _cachedColorspace = "Auto";
            }

            if (string.IsNullOrWhiteSpace(_cachedHdr))
            {
                _cachedHdr = "Unknown";
            }
        }

        private SourceColorMetadata ReadMfSourceColorMetadata(Windows.Media.MediaProperties.VideoEncodingProperties videoProps)
        {
            var result = new SourceColorMetadata { Source = "MediaFoundation" };
            var props = videoProps.Properties;
            if (props == null || props.Count == 0)
            {
                return result;
            }

            var transferFunctionGuid = new Guid("73D1072D-1870-4174-A063-29FF4FF6C11E");
            var yuvMatrixGuid = new Guid("96F66574-11C5-4015-8666-BFF516436DA7");
            var primariesGuid = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
            var nominalRangeGuid = new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");

            result.Transfer = TryReadUIntColorProperty(props, transferFunctionGuid);
            result.Matrix = TryReadUIntColorProperty(props, yuvMatrixGuid);
            result.Primaries = TryReadUIntColorProperty(props, primariesGuid);
            result.Range = TryReadUIntColorProperty(props, nominalRangeGuid);
            return result;
        }

        private SourceColorMetadata ReadStreamProxySourceColorMetadata()
        {
            var result = new SourceColorMetadata { Source = "StreamProxy" };
            var proxyColor = StreamProxyService.Instance.ColorInfo;
            if (!proxyColor.HasValue)
            {
                return result;
            }

            var pc = proxyColor.Value;
            if (!pc.HasColourDescription)
            {
                return result;
            }

            result.Primaries = (uint)pc.ColourPrimaries;
            result.Transfer = (uint)pc.TransferCharacteristics;
            result.Matrix = (uint)pc.MatrixCoefficients;
            result.Range = (uint)(pc.VideoFullRangeFlag == 1 ? 1 : 0);
            return result;
        }

        private static uint? TryReadUIntColorProperty(System.Collections.Generic.IDictionary<Guid, object> props, Guid key)
        {
            if (props.ContainsKey(key) && props[key] is uint value)
            {
                return value;
            }

            return null;
        }

        private static SourceColorMetadata MergeSourceColorMetadata(SourceColorMetadata primary, SourceColorMetadata fallback)
        {
            return new SourceColorMetadata
            {
                Primaries = ChoosePreferredColorField(SanitizeMfPrimaries(primary.Primaries), fallback.Primaries),
                Transfer = ChoosePreferredColorField(SanitizeMfTransfer(primary.Transfer), fallback.Transfer),
                Matrix = ChoosePreferredColorField(SanitizeMfMatrix(primary.Matrix), fallback.Matrix),
                Range = ChoosePreferredColorField(SanitizeMfRange(primary.Range), fallback.Range),
                Source = primary.Source
            };
        }

        private static uint? ChoosePreferredColorField(uint? primary, uint? fallback)
        {
            if (IsSpecificColorValue(primary))
            {
                return primary;
            }

            if (IsSpecificColorValue(fallback))
            {
                return fallback;
            }

            return primary ?? fallback;
        }

        private static bool IsSpecificColorValue(uint? value)
        {
            return value.HasValue && value.Value != 0 && value.Value != 2;
        }

        private static string BuildNativeColorspaceSummary(SourceColorMetadata metadata)
        {
            string matrix = DescribeMatrix(metadata.Matrix);
            string range = DescribeRange(metadata.Range);

            if (HasMeaningfulColorLabel(matrix))
            {
                return HasMeaningfulColorLabel(range) ? $"{matrix} / {range}" : matrix;
            }

            string primaries = DescribePrimaries(metadata.Primaries);
            if (HasMeaningfulColorLabel(primaries))
            {
                return HasMeaningfulColorLabel(range) ? $"{primaries} / {range}" : primaries;
            }

            return HasMeaningfulColorLabel(range) ? $"Auto / {range}" : "Auto";
        }

        private static string BuildNativeHdrSummary(SourceColorMetadata metadata, string subtype)
        {
            string transfer = DescribeTransfer(metadata.Transfer);
            string primaries = DescribePrimaries(metadata.Primaries);

            if (transfer == "HLG")
            {
                return HasMeaningfulColorLabel(primaries) ? $"HLG / {primaries}" : "HLG";
            }

            if (transfer == "HDR10 (PQ)")
            {
                return HasMeaningfulColorLabel(primaries) ? $"HDR10 (PQ) / {primaries}" : "HDR10 (PQ)";
            }

            if (HasMeaningfulColorLabel(transfer) && !transfer.StartsWith("SDR", StringComparison.OrdinalIgnoreCase))
            {
                return HasMeaningfulColorLabel(primaries) ? $"{transfer} / {primaries}" : transfer;
            }

            if (primaries == "BT.2020")
            {
                return HasMeaningfulColorLabel(transfer) && transfer != "SDR" ? $"{transfer} / {primaries}" : $"WCG / {primaries}";
            }

            bool isHevc = !string.IsNullOrEmpty(subtype) &&
                          (subtype.Contains("HEVC", StringComparison.OrdinalIgnoreCase) ||
                           subtype.Contains("HVC1", StringComparison.OrdinalIgnoreCase));

            if (isHevc)
            {
                return HasMeaningfulColorLabel(primaries) ? $"HEVC / {primaries}" : "HEVC / Unspecified";
            }

            return HasMeaningfulColorLabel(primaries) ? $"SDR / {primaries}" : "SDR / Unspecified";
        }

        private static uint? SanitizeMfPrimaries(uint? value)
        {
            return value switch
            {
                0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 22 => value,
                _ => null
            };
        }

        private static uint? SanitizeMfTransfer(uint? value)
        {
            return value switch
            {
                0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 => value,
                _ => null
            };
        }

        private static uint? SanitizeMfMatrix(uint? value)
        {
            return value switch
            {
                0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 => value,
                _ => null
            };
        }

        private static uint? SanitizeMfRange(uint? value)
        {
            return value switch
            {
                0 or 1 or 2 => value,
                _ => null
            };
        }

        private static string BuildMpvColorspaceSummary(string matrix, string range, string primaries)
        {
            string matrixText = NormalizeMpvMatrix(matrix);
            string rangeText = NormalizeMpvRange(range);
            if (matrixText != "-")
            {
                return rangeText != "-" ? $"{matrixText} / {rangeText}" : matrixText;
            }

            string primariesText = NormalizeMpvPrimaries(primaries);
            return primariesText != "-" ? primariesText : "Auto";
        }

        private static string BuildMpvHdrSummary(string primaries, string transfer)
        {
            string primariesText = NormalizeMpvPrimaries(primaries);
            string transferText = NormalizeMpvTransfer(transfer);

            if (transferText == "HLG" || transferText == "HDR10 (PQ)")
            {
                return primariesText != "-" ? $"{transferText} / {primariesText}" : transferText;
            }

            if (primariesText == "BT.2020")
            {
                return transferText != "-" && transferText != "SDR" ? $"{transferText} / {primariesText}" : $"WCG / {primariesText}";
            }

            return primariesText != "-" ? $"SDR / {primariesText}" : "SDR";
        }

        private static string DescribePrimaries(uint? value)
        {
            return value switch
            {
                1 => "BT.709",
                5 => "BT.601",
                6 => "SMPTE 240M",
                9 => "BT.2020",
                2 => "Unspecified",
                null => "-",
                _ => $"Other ({value.Value})"
            };
        }

        private static string DescribeTransfer(uint? value)
        {
            return value switch
            {
                1 => "SDR",
                4 => "Gamma 2.2",
                13 => "sRGB",
                14 => "Gamma 2.4",
                16 => "HDR10 (PQ)",
                18 => "HLG",
                2 => "Unspecified",
                null => "-",
                _ => $"Other ({value.Value})"
            };
        }

        private static string DescribeMatrix(uint? value)
        {
            return value switch
            {
                0 => "RGB",
                1 => "BT.709",
                6 => "BT.601",
                9 => "BT.2020 NCL",
                10 => "BT.2020 CL",
                2 => "Unspecified",
                null => "-",
                _ => $"Other ({value.Value})"
            };
        }

        private static string DescribeRange(uint? value)
        {
            return value switch
            {
                0 => "Limited",
                1 => "Full",
                null => "-",
                _ => $"Other ({value.Value})"
            };
        }

        private static string NormalizeMpvMatrix(string value)
        {
            value = NormalizeMpvValue(value);
            return value switch
            {
                "BT.709" => "BT.709",
                "BT.601" => "BT.601",
                "SMPTE-170M" => "BT.601",
                "BT.2020NC" => "BT.2020 NCL",
                "BT.2020-NCL" => "BT.2020 NCL",
                "BT.2020NCL" => "BT.2020 NCL",
                "BT.2020C" => "BT.2020 CL",
                "BT.2020-CL" => "BT.2020 CL",
                "BT.2020CL" => "BT.2020 CL",
                "RGB" => "RGB",
                "-" => "-",
                _ => value
            };
        }

        private static string NormalizeMpvRange(string value)
        {
            value = NormalizeMpvValue(value);
            return value switch
            {
                "LIMITED" => "Limited",
                "FULL" => "Full",
                "-" => "-",
                _ => value
            };
        }

        private static string NormalizeMpvPrimaries(string value)
        {
            value = NormalizeMpvValue(value);
            return value switch
            {
                "BT.709" => "BT.709",
                "BT.2020" => "BT.2020",
                "BT.601-525" => "BT.601",
                "BT.601-625" => "BT.601",
                "DISPLAY-P3" => "Display P3",
                "-" => "-",
                _ => value
            };
        }

        private static string NormalizeMpvTransfer(string value)
        {
            value = NormalizeMpvValue(value);
            return value switch
            {
                "HLG" => "HLG",
                "ARIB-STD-B67" => "HLG",
                "PQ" => "HDR10 (PQ)",
                "SMPTE2084" => "HDR10 (PQ)",
                "SMPTE-2084" => "HDR10 (PQ)",
                "BT.1886" => "SDR",
                "SRGB" => "SDR",
                "BT.709" => "SDR",
                "GAMMA22" => "SDR",
                "GAMMA28" => "SDR",
                "-" => "-",
                _ => value
            };
        }

        private static string NormalizeMpvValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "N/A" || value == "no")
            {
                return "-";
            }

            return value.Trim().ToUpperInvariant();
        }

        private static bool HasMeaningfulColorLabel(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value != "-" &&
                   !value.Equals("Unspecified", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHdrContentLabel(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "-")
            {
                return false;
            }

            return text.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("HLG", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("PQ", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Log the color metadata that MediaFoundation auto-detected from the HEVC bitstream.
        /// This reads the extended properties dictionary and interprets the known color GUIDs
        /// so we can see what MF's built-in HEVC decoder found in the VUI/SEI.
        /// </summary>
        private void LogAutoDetectedColorMetadata(Windows.Media.Playback.MediaPlaybackItem item, Windows.Media.MediaProperties.VideoEncodingProperties videoProps)
        {
            try
            {
                LogDiagnosticTrace("[MF-COLOR] ========== Auto-Detected Color Metadata ==========");
                LogDiagnosticTrace($"[MF-COLOR] Video: Subtype={videoProps.Subtype}, {videoProps.Width}x{videoProps.Height}, ProfileId={videoProps.ProfileId}");

                // MF attribute GUIDs from mfapi.h
                var transferFunctionGuid = new Guid("73D1072D-1870-4174-A063-29FF4FF6C11E");  // MF_MT_TRANSFER_FUNCTION
                var yuvMatrixGuid = new Guid("96F66574-11C5-4015-8666-BFF516436DA7");      // MF_MT_YUV_MATRIX
                var primariesGuid = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");      // MF_MT_VIDEO_PRIMARIES
                var nominalRangeGuid = new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");   // MF_MT_VIDEO_NOMINAL_RANGE
                var chromaSitingGuid = new Guid("696E6D74-0000-0010-8000-00AA00389B71");   // MF_MT_VIDEO_CHROMA_SITING

                var props = videoProps.Properties;
                if (props == null || props.Count == 0)
                {
                    LogDiagnosticTrace("[MF-COLOR] Extended properties: EMPTY (0 keys)");
                }
                else
                {
                    LogDiagnosticTrace($"[MF-COLOR] Extended properties: {props.Count} keys");

                    // Helper to read a uint value from the properties dictionary
                    uint? GetUint(Guid key, string name)
                    {
                        if (props.ContainsKey(key) && props[key] is uint val)
                        {
                            LogDiagnosticTrace($"[MF-COLOR]   {name} = {val}");
                            return val;
                        }
                        return null;
                    }

                    // Read color metadata values
                    uint? transfer = GetUint(transferFunctionGuid, "MF_MT_TRANSFER_FUNCTION");
                    uint? matrix = GetUint(yuvMatrixGuid, "MF_MT_YUV_MATRIX");
                    uint? primaries = GetUint(primariesGuid, "MF_MT_VIDEO_PRIMARIES");
                    uint? range = GetUint(nominalRangeGuid, "MF_MT_VIDEO_NOMINAL_RANGE");
                    uint? chromaSit = GetUint(chromaSitingGuid, "MF_MT_VIDEO_CHROMA_SITING");

                    // Also log any other properties we haven't explicitly identified
                    foreach (var kvp in props)
                    {
                        if (kvp.Key != transferFunctionGuid && kvp.Key != yuvMatrixGuid &&
                            kvp.Key != primariesGuid && kvp.Key != nominalRangeGuid && kvp.Key != chromaSitingGuid)
                        {
                            try
                            {
                                LogDiagnosticTrace($"[MF-COLOR]   {{{kvp.Key}}} = {kvp.Value} ({kvp.Value?.GetType().Name ?? "null"})");
                            }
                            catch { }
                        }
                    }

                    // Interpret the values with human-readable names
                    LogDiagnosticTrace("[MF-COLOR] --- Interpretation ---");

                    if (primaries.HasValue)
                    {
                        string pName = primaries.Value switch
                        {
                            1 => "BT.709 (SDR)",
                            5 => "BT.601 (SD)",
                            6 => "SMPTE 240M",
                            9 => "BT.2020 (HDR/WCG)",
                            2 => "Unspecified",
                            _ => $"Other ({primaries.Value})"
                        };
                        LogDiagnosticTrace($"[MF-COLOR]   Color Primaries: {pName}");
                    }
                    else
                    {
                        LogDiagnosticTrace("[MF-COLOR]   Color Primaries: NOT SET by MediaFoundation");
                    }

                    if (transfer.HasValue)
                    {
                        string tName = transfer.Value switch
                        {
                            1 => "BT.709 (SDR gamma)",
                            4 => "Gamma 2.2",
                            5 => "Gamma 2.8",
                            6 => "SMPTE 170M",
                            7 => "SMPTE 240M",
                            8 => "Linear",
                            13 => "sRGB",
                            14 => "Gamma 2.4",
                            16 => "SMPTE ST 2084 (PQ/HDR10)",
                            17 => "SMPTE ST 428-1 (DCI-P3)",
                            18 => "ARIB STD-B67 (HLG)",
                            2 => "Unspecified",
                            _ => $"Other ({transfer.Value})"
                        };
                        LogDiagnosticTrace($"[MF-COLOR]   Transfer Function: {tName}");
                    }
                    else
                    {
                        LogDiagnosticTrace("[MF-COLOR]   Transfer Function: NOT SET by MediaFoundation");
                    }

                    if (matrix.HasValue)
                    {
                        string mName = matrix.Value switch
                        {
                            1 => "BT.709 (SDR)",
                            5 => "FCC 73.684",
                            6 => "SMPTE 170M / BT.601",
                            7 => "SMPTE 240M",
                            8 => "YCgCo",
                            9 => "BT.2020_NCL (HDR/WCG)",
                            10 => "BT.2020_CL",
                            0 => "RGB (no matrix)",
                            2 => "Unspecified",
                            _ => $"Other ({matrix.Value})"
                        };
                        LogDiagnosticTrace($"[MF-COLOR]   YUV Matrix: {mName}");
                    }
                    else
                    {
                        LogDiagnosticTrace("[MF-COLOR]   YUV Matrix: NOT SET by MediaFoundation");
                    }

                    if (range.HasValue)
                    {
                        string rName = range.Value switch
                        {
                            0 => "Limited (16-235)",
                            1 => "Full (0-255)",
                            _ => $"Other ({range.Value})"
                        };
                        LogDiagnosticTrace($"[MF-COLOR]   Nominal Range: {rName}");
                    }
                    else
                    {
                        LogDiagnosticTrace("[MF-COLOR]   Nominal Range: NOT SET by MediaFoundation");
                    }

                    if (chromaSit.HasValue)
                    {
                        LogDiagnosticTrace($"[MF-COLOR]   Chroma Siting: {chromaSit.Value}");
                    }

                    // Determine if MF detected HDR
                    bool isHdr = false;
                    if (primaries == 9 || transfer == 16 || transfer == 18)
                    {
                        isHdr = true;
                        string hdrType = transfer switch
                        {
                            16 => "HDR10 (PQ)",
                            18 => "HLG (Broadcast HDR)",
                            _ => "HDR (unknown transfer)"
                        };
                        LogDiagnosticTrace($"[MF-COLOR]   *** HDR DETTECTED: {hdrType} ***");
                    }
                    else
                    {
                        LogDiagnosticTrace("[MF-COLOR]   *** SDR content (BT.709 or unspecified) ***");
                    }
                }

                // Log what the StreamProxy parser found for comparison only in verbose mode
                if (NativePlaybackVerboseDiagnostics)
                {
                    LogStreamProxyColorInfo();
                }

                LogDiagnosticTrace("[MF-COLOR] =================================================");
            }
            catch (Exception ex)
            {
                LogDiagnosticTrace($"[MF-COLOR] Auto-detect log error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Log the color info that our StreamProxy/HevcTsParser extracted from the HEVC bitstream.
        /// This lets us compare: what MF auto-detected vs what our custom parser found.
        /// </summary>
        private void LogStreamProxyColorInfo()
        {
            try
            {
                var proxyColor = StreamProxyService.Instance.ColorInfo;
                if (!proxyColor.HasValue)
                {
                    LogDiagnosticTrace("[MF-COLOR] StreamProxy: No color info parsed (parser may not have found VUI/SEI yet)");
                    return;
                }

                var pc = proxyColor.Value;
                LogDiagnosticTrace($"[MF-COLOR] StreamProxy parsed from HEVC bitstream:");
                LogDiagnosticTrace($"[MF-COLOR]   IsValid={pc.IsValid}, HasVui={pc.HasVui}, HasColourDescription={pc.HasColourDescription}");
                LogDiagnosticTrace($"[MF-COLOR]   Resolution: {pc.Width}x{pc.Height}");
                LogDiagnosticTrace($"[MF-COLOR]   ChromaFormat: {pc.ChromaFormatIdc}, BitDepth: {pc.BitDepthLuma}/{pc.BitDepthChroma}");

                if (pc.HasColourDescription)
                {
                    string pName = pc.ColourPrimaries switch { 1 => "BT.709", 9 => "BT.2020", 2 => "Unspecified", _ => $"Other ({pc.ColourPrimaries})" };
                    string tName = pc.TransferCharacteristics switch { 1 => "BT.709", 16 => "PQ/HDR10", 18 => "HLG", 2 => "Unspecified", _ => $"Other ({pc.TransferCharacteristics})" };
                    string mName = pc.MatrixCoefficients switch { 1 => "BT.709", 9 => "BT.2020_NCL", 2 => "Unspecified", _ => $"Other ({pc.MatrixCoefficients})" };
                    string rName = pc.VideoFullRangeFlag == 1 ? "Full" : "Limited";

                    LogDiagnosticTrace($"[MF-COLOR]   Primaries={pc.ColourPrimaries} ({pName})");
                    LogDiagnosticTrace($"[MF-COLOR]   Transfer={pc.TransferCharacteristics} ({tName})");
                    LogDiagnosticTrace($"[MF-COLOR]   Matrix={pc.MatrixCoefficients} ({mName})");
                    LogDiagnosticTrace($"[MF-COLOR]   Range={rName}");

                    bool isHdr = pc.ColourPrimaries == 9 || pc.TransferCharacteristics == 16 || pc.TransferCharacteristics == 18;
                    if (isHdr)
                    {
                        string hdrType = pc.TransferCharacteristics switch { 16 => "HDR10 (PQ)", 18 => "HLG", _ => "HDR" };
                        LogDiagnosticTrace($"[MF-COLOR]   *** StreamProxy detects HDR: {hdrType} ***");
                    }
                }
                else
                {
                    LogDiagnosticTrace($"[MF-COLOR]   *** No colour description in VUI (HasColourDescription=False) ***");
                    LogDiagnosticTrace($"[MF-COLOR]   This means the stream's SPS has vui_parameters_present_flag=0 or colour_description_present_flag=0");
                }
            }
            catch (Exception ex)
            {
                LogDiagnosticTrace($"[MF-COLOR] StreamProxy log error: {ex.Message}");
            }
        }

        private async void InjectColorMetadata(Windows.Media.Playback.MediaPlaybackItem item, Windows.Media.MediaProperties.VideoEncodingProperties videoProps)
        {
            try
            {
                Debug.WriteLine("[MF-COLOR] Attempting HDR color metadata injection...");
                Debug.WriteLine($"[MF-COLOR] Video: Subtype={videoProps.Subtype}, Width={videoProps.Width}, Height={videoProps.Height}, Bitrate={videoProps.Bitrate}");

                (int ColorPrimaries, int ColorTransfer, int ColorMatrix, int ColorRange, int Success)? colorInfo = null;

                // === PATH 1: Proxy-parsed SEI/SPS color metadata (zero latency) ===
                var proxyColor = StreamProxyService.Instance.ColorInfo;
                if (proxyColor.HasValue && proxyColor.Value.HasColourDescription)
                {
                    var pc = proxyColor.Value;
                    Debug.WriteLine($"[MF-COLOR] PATH 1: Proxy parsed color from HEVC bitstream: Primaries={pc.ColourPrimaries}, Transfer={pc.TransferCharacteristics}, Matrix={pc.MatrixCoefficients}");

                    colorInfo = (
                        ColorPrimaries: pc.ColourPrimaries,
                        ColorTransfer: pc.TransferCharacteristics,
                        ColorMatrix: pc.MatrixCoefficients,
                        ColorRange: pc.VideoFullRangeFlag,
                        Success: 1
                    );
                }

                // === PATH 2: Detect 10-bit HEVC from MF video properties ===
                if (!colorInfo.HasValue)
                {
                    var props2 = videoProps.Properties;
                    bool is10Bit = false;
                    string codecSub = videoProps.Subtype?.ToUpperInvariant() ?? "";
                    bool isHevc = codecSub.Contains("H265") || codecSub.Contains("HEVC") || codecSub.Contains("48323645") || codecSub.Contains("265");

                    if (props2 != null)
                    {
                        var subtypeGuid = new Guid("C9498D84-359E-4C65-9F35-80B3B88C8D4E"); // MF_MT_SUBTYPE
                        if (props2.ContainsKey(subtypeGuid))
                        {
                            var subtypeVal = props2[subtypeGuid];
                            string subtypeStr = subtypeVal?.ToString()?.ToUpperInvariant() ?? "";
                            Debug.WriteLine($"[MF-COLOR] MF_MT_SUBTYPE GUID: {subtypeStr}");

                            is10Bit = subtypeStr.Contains("43303050") || // P010
                                      subtypeStr.Contains("3031324E") || // NV12_10
                                      subtypeStr.Contains("P010") ||
                                      subtypeStr.Contains("NV12");
                        }

                        Debug.WriteLine($"[MF-COLOR] Codec subtype: {codecSub}, isHEVC={isHevc}, is10Bit={is10Bit}");

                        var profileGuid = new Guid("E28E349E-1D6A-4C23-B0F0-6D1F4C6D5D57"); // MF_MT_MPEG2_PROFILE
                        if (props2.ContainsKey(profileGuid))
                        {
                            var profileVal = props2[profileGuid];
                            Debug.WriteLine($"[MF-COLOR] MF_MT_MPEG2_PROFILE: {profileVal}");
                            if (profileVal is uint profile && profile == 2) // HEVC_Main10 = 2
                            {
                                is10Bit = true;
                                Debug.WriteLine("[MF-COLOR] Detected HEVC Main10 profile from MF attributes");
                            }
                        }
                    }

                    if (!is10Bit)
                    {
                        bool isLive = _streamUrl.Contains("/live/") || _streamUrl.Contains(".m3u8") || _streamUrl.Contains(":8080") || _streamUrl.Contains("/ts/");
                        bool isHighRes = videoProps.Width >= 1920 || videoProps.Height >= 1080;

                        if (isLive && isHevc && isHighRes)
                        {
                            Debug.WriteLine($"[MF-COLOR] PATH 2: Heuristic detected - Live IPTV + HEVC + {videoProps.Width}x{videoProps.Height} → Assuming HDR HLG");
                            is10Bit = true;
                        }
                    }

                    if (is10Bit && isHevc)
                    {
                        colorInfo = (
                            ColorPrimaries: 9,
                            ColorTransfer: 18,
                            ColorMatrix: 9,
                            ColorRange: 0,
                            Success: 1
                        );
                        Debug.WriteLine("[MF-COLOR] PATH 2: Detected 10-bit HEVC, injecting BT.2020/HLG HDR metadata");
                    }
                    else
                    {
                        Debug.WriteLine("[MF-COLOR] PATH 2: No 10-bit HEVC detected (SDR or unknown format)");
                    }
                }

                // === Inject color metadata into MF pipeline ===
                if (!colorInfo.HasValue || colorInfo.Value.Success != 1)
                {
                    Debug.WriteLine("[MF-COLOR] No HDR metadata available (SDR content or all detection methods failed)");
                    return;
                }

                var info = colorInfo.Value;
                bool isHdr = info.ColorPrimaries == 9 || info.ColorTransfer == 16 || info.ColorTransfer == 18;
                if (!isHdr)
                {
                    Debug.WriteLine("[MF-COLOR] Detected SDR content (BT.709), no HDR injection needed");
                    return;
                }

                Debug.WriteLine($"[MF-COLOR] Injecting HDR metadata into MediaFoundation:");
                Debug.WriteLine($"[MF-COLOR]   Primaries={info.ColorPrimaries} ({(info.ColorPrimaries == 9 ? "BT.2020" : "Other")})");
                Debug.WriteLine($"[MF-COLOR]   Transfer={info.ColorTransfer} ({(info.ColorTransfer == 18 ? "HLG" : info.ColorTransfer == 16 ? "PQ/HDR10" : "Other")})");
                Debug.WriteLine($"[MF-COLOR]   Matrix={info.ColorMatrix} ({(info.ColorMatrix == 9 ? "BT.2020_NCL" : "Other")})");
                Debug.WriteLine($"[MF-COLOR]   Range={info.ColorRange} ({(info.ColorRange == 1 ? "Full" : "Limited")})");

                var transferFunctionGuid = new Guid("73D1072D-1870-4174-A063-29FF4FF6C11E");  // MF_MT_TRANSFER_FUNCTION
                var yuvMatrixGuid = new Guid("96F66574-11C5-4015-8666-BFF516436DA7");      // MF_MT_YUV_MATRIX
                var primariesGuid = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");      // MF_MT_VIDEO_PRIMARIES
                var nominalRangeGuid = new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");   // MF_MT_VIDEO_NOMINAL_RANGE

                var props = videoProps.Properties;
                if (props != null)
                {
                    props[transferFunctionGuid] = (uint)info.ColorTransfer;
                    props[yuvMatrixGuid] = (uint)info.ColorMatrix;
                    props[primariesGuid] = (uint)info.ColorPrimaries;
                    props[nominalRangeGuid] = (uint)info.ColorRange;

                    Debug.WriteLine("[MF-COLOR] HDR color metadata injected successfully into VideoEncodingProperties");
                }
                else
                {
                    Debug.WriteLine("[MF-COLOR] WARNING: Video encoding properties is null, cannot inject HDR metadata");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MF-COLOR] HDR metadata injection error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void AdaptiveSource_DownloadCompleted(Windows.Media.Streaming.Adaptive.AdaptiveMediaSource sender, Windows.Media.Streaming.Adaptive.AdaptiveMediaSourceDownloadCompletedEventArgs args)
        {
            ulong? bytes = args.ResourceByteRangeLength;
            if (!bytes.HasValue || bytes.Value == 0) return;
            ulong downloadedBytes = bytes.Value;

            lock (this)
            {
                var now = DateTime.Now;
                if (_downloadSpeedWindowStart == DateTime.MinValue)
                {
                    _downloadSpeedWindowStart = now;
                    _downloadSpeedWindowBytes = downloadedBytes;
                }
                else
                {
                    var elapsed = (now - _downloadSpeedWindowStart).TotalSeconds;
                    if (elapsed > 5.0)
                    {
                        _downloadSpeedWindowStart = now;
                        _downloadSpeedWindowBytes = 0;
                    }
                    _downloadSpeedWindowBytes += downloadedBytes;
                    elapsed = (now - _downloadSpeedWindowStart).TotalSeconds;
                    if (elapsed > 0)
                    {
                        _downloadSpeedBytes = (ulong)(_downloadSpeedWindowBytes / elapsed);
                    }
                }
            }
        }

        private void TriggerMpvFallback()
        {
            if (_useMpvPlayer) return; // Already in MPV
            ShowOsd("Native Oynatıcı desteklemiyor, MPV ile deneniyor...");
            
            // Fix Persistence: Assign back to AppSettings to ensure it sticks
            var settings = AppSettings.PlayerSettings;
            settings.Engine = Models.PlayerEngine.Mpv;
            AppSettings.PlayerSettings = settings;

            _useMpvPlayer = true;
            
            BeginNativeTeardown(collapseNativeSurface: true, reason: "fallback-to-mpv");

            // Restart playback flow with a minor delay to avoid GPU race conditions
            _isPageLoaded = false;
            _ = Task.Run(async () => {
                try
                {
                    await Task.Delay(150);
                    DispatcherQueue.TryEnqueue(() => {
                        PlayerPage_Loaded(this, new RoutedEventArgs());
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlayerPage] Reload error: {ex.Message}");
                }
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetSystemCursor(IntPtr hcur, uint id);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CopyIcon(IntPtr hcur);

        private const uint OCR_WAIT = 32514;
        private const uint OCR_APPSTARTING = 32516; // Arrow + Wait
        private static IntPtr _originalWaitCursor = IntPtr.Zero;
        private static IntPtr _originalAppStarting = IntPtr.Zero;
        private static IntPtr _blankHandle = IntPtr.Zero;

        private void SetCursorVisible(bool visible)
        {
            System.Diagnostics.Debug.WriteLine($"[CURSOR] SetCursorVisible({visible})");
            _isCursorHidden = !visible;

            if (!visible)
            {
                try {
                    // 1. Create and CACHE a blank cursor so it stays in memory
                    if (_blankHandle == IntPtr.Zero) {
                        byte[] andMask = new byte[128]; for(int i=0; i<128; i++) andMask[i] = 0xFF;
                        byte[] xorMask = new byte[128];
                        _blankHandle = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
                    }

                    // 2. Backup and Replace OCR_WAIT and OCR_APPSTARTING
                    if (_originalWaitCursor == IntPtr.Zero) {
                        _originalWaitCursor = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)OCR_WAIT));
                    }
                    if (_originalAppStarting == IntPtr.Zero) {
                        _originalAppStarting = CopyIcon(LoadCursor(IntPtr.Zero, (IntPtr)OCR_APPSTARTING));
                    }

                    // Force blanking
                    SetSystemCursor(CopyIcon(_blankHandle), OCR_WAIT);
                    SetSystemCursor(CopyIcon(_blankHandle), OCR_APPSTARTING);

                    // 3. Tell WinUI to use WAIT (which is now invisible)
                    var invisibleCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Wait);
                    ProtectedCursor = invisibleCursor;
                    if (MainGrid != null) {
                        _protectedCursorProp?.SetValue(MainGrid, invisibleCursor);
                        int count = 0;
                        HibernateChildHitTest(MainGrid, true, ref count);
                    }
                } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CURSOR] Trojan fail: {ex.Message}"); }
            }
            else
            {
                // 1. Restore WAIT and APPSTARTING cursors to system
                if (_originalWaitCursor != IntPtr.Zero) SetSystemCursor(_originalWaitCursor, OCR_WAIT);
                if (_originalAppStarting != IntPtr.Zero) SetSystemCursor(_originalAppStarting, OCR_APPSTARTING);
                
                // Keep the original pointers for the next session if needed, 
                // OR set to Zero if you want to re-backup next time.
                // Keep them Zero to ensure we always grab the latest system state.
                _originalWaitCursor = IntPtr.Zero;
                _originalAppStarting = IntPtr.Zero;

                // 2. Restore WinUI: Setting to null clears the override
                ProtectedCursor = null; 
                if (MainGrid != null) {
                    _protectedCursorProp?.SetValue(MainGrid, null);
                    int count = 0;
                    HibernateChildHitTest(MainGrid, false, ref count);
                }
            }
        }

        private static readonly System.Reflection.PropertyInfo? _protectedCursorProp =
            typeof(UIElement).GetProperty("ProtectedCursor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        private static void HibernateChildHitTest(UIElement element, bool hibernate, ref int count)
        {
            int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childCount; i++)
            {
                if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i) is UIElement child)
                {
                    bool shouldHibernate = hibernate;

                    // If we're hiding, don't disable hit-test for the base video layers
                    // so we can still catch PointerMoved events on MainGrid.
                    if (hibernate && child is FrameworkElement fe)
                    {
                        if (fe.Name == "PlayerContainer" || fe.Name == "BrightnessOverlay" || fe.Name == "MediaFoundationPlayer")
                        {
                            shouldHibernate = false;
                        }
                    }

                    child.IsHitTestVisible = !shouldHibernate;
                    if (shouldHibernate) count++;
                    HibernateChildHitTest(child, hibernate, ref count);
                }
            }
        }

        private void RemoveCursorHook()
        {
            if (_isCursorHidden) SetCursorVisible(true);
        }

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
            _isDragging = false;
            _lastSeekTime = DateTime.Now;
            var val = SeekSlider.Value;
            
            if (_useMpvPlayer && _mpvPlayer != null)
            {
                await _mpvPlayer.ExecuteCommandAsync("seek", val.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }
            else if (!_useMpvPlayer && _nativeMediaPlayer != null)
            {
                _nativeMediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(val);
            }
        }

        private void SeekSlider_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Just reset the flag, don't trigger a seek if capture was lost involuntarily
            _isDragging = false;
        }

        private async void SeekSlider_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
             e.Handled = true;
             if (SeekSlider.ActualWidth > 0)
             {
                 var pos = e.GetPosition(SeekSlider);
                 var ratio = pos.X / SeekSlider.ActualWidth;
                 var newVal = ratio * SeekSlider.Maximum;
                 SeekSlider.Value = newVal;

                 _lastSeekTime = DateTime.Now;
                 if (_useMpvPlayer && _mpvPlayer != null)
                 {
                     await _mpvPlayer.ExecuteCommandAsync("seek", newVal.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
                 }
                 else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                 {
                     _nativeMediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(newVal);
                 }
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
             if (_useMpvPlayer && _mpvPlayer != null)
             {
                 // Check if seekable
                 var seekable = await _mpvPlayer.GetPropertyAsync("seekable");

                 if (seekable == "no")
                 {
                     ShowOsd("YAYIN YENİDEN YÜKLENİYOR (LIVE)...");
                     await _mpvPlayer.OpenAsync(_streamUrl);
                     await _mpvPlayer.SetPropertyAsync("pause", "no");

                     LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                     LiveText.Text = "CANLI";
                     return;
                 }

                 ShowOsd("CANLI YAYINA GİDİLİYOR...");
                 await _mpvPlayer.SetPropertyAsync("pause", "no");
                 _isBehind = false;
                  _sessionStartTime = DateTime.Now; // Post-seek immunity

                 ShowOsd("SEEKING TO LIVE...");
                 await LogStatus("PRE-LIVE-SEEK");
                 await _mpvPlayer.ExecuteCommandAsync("seek", "100", "absolute-percent+exact");
                 await LogStatus("POST-LIVE-SEEK");

                 LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                 LiveText.Text = "CANLI";
             }
             else if (!_useMpvPlayer && _nativeMediaPlayer != null)
             {
                 // MF: Handle Live refresh
                 var session = _nativeMediaPlayer.PlaybackSession;
                 if (session == null) return;

                 ShowOsd("YAYIN YENİDEN YÜKLENİYOR (LIVE)...");

                 // 1. Show loading overlay IMMEDIATELY (user request)
                 DispatcherQueue.TryEnqueue(() =>
                 {
                     StartLogoLoading();
                     ShowBufferingOverlay();
                 });

                 _sessionStartTime = DateTime.Now;

                 // 2. Identify if seeking to end is viable (Fast path)
                 bool canFastSeek = session.CanSeek && session.NaturalDuration > TimeSpan.Zero;

                 if (canFastSeek)
                 {
                     try
                     {
                         LogPlayerTrace("[PlayerPage] Live: Attempting Fast-Seek to live edge...");
                         session.Position = session.NaturalDuration;
                         _isBehind = false;
                  _sessionStartTime = DateTime.Now; // Post-seek immunity
                         
                         // Indicators update (will be confirmed by StatsTimer calling StopLogoLoading)
                         DispatcherQueue.TryEnqueue(() =>
                         {
                             LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                             LiveText.Text = "CANLI";
                         });
                         return;
                     }
                     catch (Exception ex)
                     {
                         LogPlayerTrace($"[PlayerPage] Live: Fast-Seek failed ({ex.Message}). Falling back to Hard-Reload.");
                     }
                 }

                 // 3. Hard-Reload (Slow path but guaranteed to be high speed like initial open)
                 LogPlayerTrace("[PlayerPage] Live: Performing Hard-Reload for native player...");
                 
                 // We let the new player session handle hiding the loading via StatsTimer/MediaOpened.
                 _ = StartNativePlaybackAsync(_streamUrl);

                 DispatcherQueue.TryEnqueue(() =>
                 {
                     LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                     LiveText.Text = "CANLI";
                 });
             }
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
             if (!RewindButton.IsEnabled) return;
             if (_useMpvPlayer && _mpvPlayer == null) return;
             if (!_useMpvPlayer && _nativeMediaPlayer == null) return;
             
             if (LiveButton.Visibility == Visibility.Visible) _isBehind = true; _pendingSeekSeconds -= AppSettings.SeekBackwardSeconds;
             ShowOsd($"{(_pendingSeekSeconds > 0 ? "+" : "")}{_pendingSeekSeconds} SANİYE");
             
             _seekDebounceTimer.Stop(); // Reset timer
             _seekDebounceTimer.Start();
        }

        private async void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
             if (!FastForwardButton.IsEnabled) return;
             if (_useMpvPlayer && _mpvPlayer == null) return;
             if (!_useMpvPlayer && _nativeMediaPlayer == null) return;
             
             _pendingSeekSeconds += AppSettings.SeekForwardSeconds;
             ShowOsd($"{(_pendingSeekSeconds > 0 ? "+" : "")}{_pendingSeekSeconds} SANİYE");
             
             _seekDebounceTimer.Stop(); // Reset timer
             _seekDebounceTimer.Start();
        }
        
        private async void SeekDebounceTimer_Tick(object? sender, object e)
        {
             _seekDebounceTimer.Stop();
             if (_pendingSeekSeconds == 0) return;

             try
             {
                 int seekVal = _pendingSeekSeconds;
                 _pendingSeekSeconds = 0; // Reset pending immediately

                 if (seekVal < 0) _isBehind = true;

                 ShowOsd($"GİDİLİYOR: {(seekVal > 0 ? "+" : "")}{seekVal}sn");
                 
                 if (_useMpvPlayer && _mpvPlayer != null)
                 {
                     await LogStatus($"PRE-DEBOUNCE-SEEK({seekVal})");
                     await _mpvPlayer.ExecuteCommandAsync("seek", seekVal.ToString(), "relative");
                     await LogStatus($"POST-DEBOUNCE-SEEK({seekVal})");
                 }
                else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                {
                    var session = _nativeMediaPlayer.PlaybackSession;
                    if (session != null && session.CanSeek)
                    {
                        var targetPos = session.Position + TimeSpan.FromSeconds(seekVal);
                        
                        // Safety bounding
                        if (targetPos < TimeSpan.Zero) targetPos = TimeSpan.Zero;
                        if (session.NaturalDuration > TimeSpan.Zero && targetPos > session.NaturalDuration)
                             targetPos = session.NaturalDuration;
                             
                        session.Position = targetPos;
                        _lastSeekTime = DateTime.Now; // Update last seek to prevent UI jump back
                    }
                }
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
            if (_mpvPlayer == null && _nativeMediaPlayer == null) return;

            _currentAspectIndex = (_currentAspectIndex + 1) % _aspectRatios.Length;
            string newAspect = _aspectRatios[_currentAspectIndex];
            string aspectName = _aspectNames[_currentAspectIndex];

            if (_useMpvPlayer && _mpvPlayer != null)
            {
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
            }
            else if (!_useMpvPlayer && _nativeMediaPlayer != null)
            {
                if (newAspect == "fill")
                {
                    MediaFoundationPlayer.Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill;
                }
                else if (newAspect == "2.35:1")
                {
                    // Approximate Cinema aspect ratio by cropping top/bottom slightly
                    MediaFoundationPlayer.Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill;
                }
                else
                {
                    // Auto, 16:9, 4:3 default to Uniform for native
                    MediaFoundationPlayer.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
                }
            }
            
            ShowOsd($"Görüntü: {aspectName}");
        }

        // ---------- VOLUME CONTROL ----------
        private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            double val = e.NewValue;

            if (_useMpvPlayer && _mpvPlayer != null)
            {
                // Auto-unmute if dragging
                await _mpvPlayer.SetPropertyAsync("mute", "no");
                await _mpvPlayer.SetPropertyAsync("volume", val.ToString(CultureInfo.InvariantCulture));
            }
            else if (!_useMpvPlayer && _nativeMediaPlayer != null)
            {
                _nativeMediaPlayer.IsMuted = false;
                _nativeMediaPlayer.Volume = val / 100.0;
            }

            UpdateVolumeIcon(val, false);
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_useMpvPlayer && _mpvPlayer != null)
            {
                try
                {
                    string muteState = await _mpvPlayer.GetPropertyAsync("mute");
                    bool isMuted = (muteState == "yes");

                    if (isMuted)
                    {
                        await _mpvPlayer.SetPropertyAsync("mute", "no");
                    }
                    else
                    {
                        await _mpvPlayer.SetPropertyAsync("mute", "yes");
                    }

                    // Update all mute icons on UI thread
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        bool nowMuted = !isMuted;
                        // PipMuteIcon: Show what WILL happen on next click (action indicator)
                        if (PipMuteIcon != null)
                        {
                            PipMuteIcon.Glyph = nowMuted ? "\uE74F" : "\uE767";
                        }
                        UpdateVolumeIcon(VolumeSlider.Value, nowMuted);
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PiP] Mute Error: {ex.Message}");
                }
            }
            else if (!_useMpvPlayer && _nativeMediaPlayer != null)
            {
                _nativeMediaPlayer.IsMuted = !_nativeMediaPlayer.IsMuted;
                UpdateVolumeIcon(VolumeSlider.Value, _nativeMediaPlayer.IsMuted);
            }
        }

        private void UpdateVolumeIcon(double volume, bool isMuted)
        {
            // Main Button: Status Indicator (What is the current state?)
            if (VolumeIcon != null)
            {
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
            }

            // Overlay Mute Button: Action Indicator (What will happen when clicked?)
            // If Muted -> Show Speaker (Unmute)
            // If Sound -> Show Cross (Mute)
            if (isMuted || volume == 0)
            {
                 if (MuteIcon != null) MuteIcon.Glyph = "\uE767"; // Volume Icon (Speaker) -> Unmute
                 if (PipMuteIcon != null) PipMuteIcon.Glyph = "\uE767";
            }
            else
            {
                 if (MuteIcon != null) MuteIcon.Glyph = "\uE74F"; // Mute Icon (Cross) -> Mute
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
            if (_useMpvPlayer)
            {
                if (_mpvPlayer == null) return;
                try
                {
                    bool isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                    bool newState = !isPaused;
                    await _mpvPlayer.SetPropertyAsync("pause", newState ? "yes" : "no");
                    PlayPauseIcon.Glyph = newState ? "\uF5B0" : "\uF8AE";
                    if (PipPlayPauseIcon != null) PipPlayPauseIcon.Glyph = newState ? "\uF5B0" : "\uF8AE";
                }
                catch { }
            }
            else if (_nativeMediaPlayer != null)
            {
                if (_nativeMediaPlayer.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                    _nativeMediaPlayer.Pause();
                else
                    _nativeMediaPlayer.Play();

                bool isNowPaused = _nativeMediaPlayer.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing;
                PlayPauseIcon.Glyph = isNowPaused ? "\uF8AE" : "\uF5B0";
                if (PipPlayPauseIcon != null) PipPlayPauseIcon.Glyph = isNowPaused ? "\uF8AE" : "\uF5B0";
            }
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
            if (_suppressPointerReveal) return;

            if (_isPiPMode)
            {
                // In PiP mode, ensure cursor is visible and forward to PipOverlay for hover detection
                if (_isCursorHidden)
                {
                    SetCursorVisible(true);
                }
                
                if (!_pipControlsVisible)
                {
                    AnimatePipOverlay(true);
                }
                return;
            }

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

        private void MainGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isPiPMode)
            {
                return;
            }

            // Re-arm the normal player hover lifecycle after PiP exit or any shell-driven transition.
            _isDragging = false;
            _isPointerDragging = false;
            _isGestureTarget = false;
            
            // Don't reveal controls on entry - only reset the timer.
            // Controls should only appear on actual hover (PointerMoved), not when entering
            // from the title bar or sidebar area. This prevents flicker.
            if (_isCursorHidden)
            {
                SetCursorVisible(true);
            }
            _cursorTimer?.Stop();
            _cursorTimer?.Start();
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
            if (_isPiPMode)
            {
                return;
            }

            HideControls();
        }

        // ---------- CURSOR & CONTROLS AUTO-HIDE LOGIC ----------





        private void StartCursorTimer()
        {
            ResumeAutoHideLifecycle(revealControls: true);
        }

        private void ResumeAutoHideLifecycle(bool revealControls)
        {
            if (_cursorTimer == null) return;

            _cursorTimer.Stop();
            _cursorTimer.Start();

            if (revealControls && _controlsHidden)
            {
                SetControlsVisibility(Visibility.Visible);
            }
        }

        private void StopCursorTimer()
        {
            _cursorTimer?.Stop();
            SetControlsVisibility(Visibility.Visible);
        }

        private void ResetCursorTimer()
        {
            // Don't reveal controls during window resize
            if (_isResizingWindow) return;
            ResumeAutoHideLifecycle(revealControls: true);
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

        private void PlayerPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isResizingWindow)
            {
                _isResizingWindow = true;
                // Sürükleme başladığında ağır XAML UI bileşenlerini gizleyerek kasıntıyı önle
                SetControlsVisibility(Visibility.Collapsed);

                // [ÖNEMLİ] SwapChain'in kopmasını (floating) ve ışınlanmasını önlemek için 
                // XAML boyutlarını boyutlandırma boyunca ilk anki haline kilitleriz. 
                // Bu sayede donanım SwapChain render işlemi felç olmaz.
                if (MediaFoundationPlayer != null && MediaFoundationPlayer.Visibility == Visibility.Visible)
                {
                    if (MediaFoundationPlayer.ActualWidth > 0 && MediaFoundationPlayer.ActualHeight > 0)
                    {
                    }
                }
            }

            _resizeDebounceTimer.Stop();
            _resizeDebounceTimer.Start();
        }

        private void ResizeDebounceTimer_Tick(object sender, object e)
        {
            _resizeDebounceTimer.Stop();
            _isResizingWindow = false;

            if (MediaFoundationPlayer != null)
            {
                // Kilitleri kaldır: XAML artık son ulaşılan boyuta göre tek bir sefer SwapChain buffer yenilemesi yapsın.
            }

            // Don't reveal controls on resize end - only start auto-hide timer.
            // Controls should only appear on actual hover, not just because resize finished.
            _cursorTimer?.Stop();
            _cursorTimer?.Start();
        }

        private void SetControlsVisibility(Visibility visibility)
        {
            if (ControlsBorder == null || BackButton == null) return;

            if (_isPiPMode && visibility == Visibility.Visible)
            {
                visibility = Visibility.Collapsed;
            }

            bool shouldBeHidden = visibility == Visibility.Collapsed;
            bool needsUpdate =
                ControlsBorder.Visibility != visibility ||
                BackButton.Visibility != visibility ||
                VideoTitleText.Visibility != visibility ||
                FullScreenButton.Visibility != visibility ||
                _controlsHidden != shouldBeHidden;

            if (needsUpdate)
            {
                ControlsBorder.Visibility = visibility;
                BackButton.Visibility = visibility;
                VideoTitleText.Visibility = visibility;
                FullScreenButton.Visibility = visibility;

                if (visibility == Visibility.Visible)
                {
                    // Debounce info pills animation - prevent rapid re-shows when pointer
                    // moves between title bar and player area
                    bool canShowPills = !_isInfoPillsAnimating &&
                        (DateTimeOffset.Now - _lastInfoPillsShowTime).TotalMilliseconds > 500;
                    
                    if (InfoPillsStack.Visibility != Visibility.Visible && canShowPills)
                    {
                        InfoPillsStack.Visibility = Visibility.Visible;
                        _isInfoPillsAnimating = true;
                        _lastInfoPillsShowTime = DateTimeOffset.Now;
                        
                        // Reset flag after animation completes
                        ShowInfoPillsAnim.Completed += (s, e) => _isInfoPillsAnimating = false;
                        ShowInfoPillsAnim.Begin();
                    }
                    else if (InfoPillsStack.Visibility != Visibility.Visible)
                    {
                        // Just show without animation if debouncing
                        InfoPillsStack.Visibility = Visibility.Visible;
                        InfoPillsStack.Opacity = 1;
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
                    // Reset animation state so pills can animate fresh next time controls appear
                    _isInfoPillsAnimating = false;
                }

                _controlsHidden = (visibility == Visibility.Collapsed);

                // Mouse Cursor Auto-Hide Logic (DISABLED in PiP mode)
                if (!_isPiPMode)
                {
                    if (_controlsHidden && !_isCursorHidden)
                    {
                        SetCursorVisible(false);
                    }
                    else if (!_controlsHidden && _isCursorHidden)
                    {
                        SetCursorVisible(true);
                    }
                }
            }
        }



        /// <summary>
        /// Detects if a URL is likely a live stream based on common IPTV patterns.
        /// </summary>
        private bool IsLikelyLiveUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var lower = url.ToLower();
            
            // Common live stream indicators
            if (lower.Contains("/live/") || lower.Contains("/live.") || lower.Contains("?type=live"))
                return true;
            
            // HLS live playlist
            if (lower.Contains(".m3u8") && (lower.Contains("/live/") || lower.Contains("/play/")))
                return true;
            
            // IPTV service patterns
            if (lower.Contains(":8080") || lower.Contains(":8000") || lower.Contains("/stream/"))
                return true;
            
            // Xtream Codes live format: /live/user/pass/channelId.ext
            if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"/live/[^/]+/[^/]+/\d+"))
                return true;
            
            return false;
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
        private bool IsPlayerActive => _isPageLoaded && (_mpvPlayer != null || (_nativeMediaPlayer != null && MediaFoundationPlayer.Visibility == Visibility.Visible));

        // ---------- SUBTITLE & SYNC STATE ----------

        private bool _isPopulatingTracks = false;





        private async Task LoadTracksAsync()
        {
            if (!IsPlayerActive) return;

            _isPopulatingTracks = true;
            
            // Route to player-specific implementation
            if (_useMpvPlayer && _mpvPlayer != null)
            {
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] Started (MPV Mode).");
                await LoadTracksAsync_Mpv();
            }
            else if (!_useMpvPlayer && _nativeMediaPlayer != null)
            {
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] Started (MediaFoundation Mode).");
                await LoadTracksAsync_Native();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync] No active player found.");
            }

            _isPopulatingTracks = false;
        }

        private async Task LoadTracksAsync_Mpv()
        {
            try
            {
                // 1. Get Track Count
                string sCount = await _mpvPlayer.GetPropertyAsync("track-list/count");
                if (!int.TryParse(sCount, out int trackCount) || trackCount <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Mpv] No tracks found.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync_Mpv] Count: {trackCount}");

                var audioTracks = new List<TrackItem>();
                var newSubTracks = new List<SubtitleTrackViewModel>();

                // 2. Iterate with Safety Delay
                for (int i = 0; i < trackCount; i++)
                {
                    // Exit if player was detached/nullified (e.g. Back navigation)
                    if (!IsPlayerActive) break;

                    // Throttle calls to prevent Heap Corruption (0xC0000374)
                    await Task.Delay(15); 
                    
                    if (!IsPlayerActive) break;

                    string type = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/type");
                    if (!IsPlayerActive) break;
                    
                    if (type == "audio")
                    {
                        string sId = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/id");
                        if (!IsPlayerActive) break;
                        int.TryParse(sId, out int id);
                        
                        bool selected = await _mpvPlayer.GetPropertyBoolAsync($"track-list/{i}/selected");
                        if (!IsPlayerActive) break;
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
                        if (!IsPlayerActive) break;
                        int.TryParse(sId, out int id);
                        
                        string title = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/title");
                        string lang = await _mpvPlayer.GetPropertyAsync($"track-list/{i}/lang");

                        if (title == "N/A") title = "";
                        if (lang == "N/A") lang = "";

                        bool subSelected = await _mpvPlayer.GetPropertyBoolAsync($"track-list/{i}/selected");

                        newSubTracks.Add(new SubtitleTrackViewModel 
                        { 
                            Id = id, 
                            Text = string.IsNullOrEmpty(title) ? $"Track {id}" : title,
                            Lang = !string.IsNullOrEmpty(lang) ? lang : "und",
                            IsAddon = false,
                            IsSelected = subSelected
                        });
                    }
                }

                // Update Audio UI
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Mpv] Updating Audio UI...");
                if (_isPageLoaded && AudioListView != null)
                {
                    AudioListView.ItemsSource = audioTracks;
                    var selectedAudio = audioTracks.FirstOrDefault(t => t.IsSelected);
                    if (selectedAudio != null) AudioListView.SelectedItem = selectedAudio;
                }

                // Update Subtitle UI (Merge new tracks, keep addons)
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Mpv] Updating Subtitle UI...");

                // Preserve addons
                var existingAddons = _currentSubtitleTracks.Where(t => t.IsAddon).ToList();
                _currentSubtitleTracks = newSubTracks;
                _currentSubtitleTracks.AddRange(existingAddons);

                if (_isPageLoaded)
                {
                    UpdateLanguageList(isLoading: true);
                }

                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Mpv] Completed Successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync_Mpv] CRITICAL ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync_Mpv] StackTrace: {ex.StackTrace}");
            }
            finally
            {
                _isPopulatingTracks = false;
            }
        }

        private async Task LoadTracksAsync_Native()
        {
            try
            {
                // Use MediaFoundation's MediaPlaybackItem to enumerate tracks
                if (_nativeMediaPlayer?.Source is not Windows.Media.Playback.MediaPlaybackItem playbackItem)
                {
                    System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Native] No MediaPlaybackItem available.");
                    return;
                }

                var audioTracks = new List<TrackItem>();
                var newSubTracks = new List<SubtitleTrackViewModel>();

                // Enumerate audio tracks
                var mfAudioTracks = playbackItem.AudioTracks;
                if (mfAudioTracks != null)
                {
                    for (int i = 0; i < mfAudioTracks.Count; i++)
                    {
                        var track = mfAudioTracks[i];
                        var encodingProps = track.GetEncodingProperties();
                        
                        string lang = track.Language ?? $"Track {i + 1}";
                        string title = track.Label ?? "";
                        string codec = encodingProps?.Subtype ?? "";
                        uint channels = encodingProps?.ChannelCount ?? 0;
                        
                        bool isSelected = (i == playbackItem.AudioTracks.SelectedIndex);
                        
                        string details = "";
                        if (!string.IsNullOrEmpty(codec)) details += codec;
                        if (channels > 0) details += $" {channels}ch";
                        
                        string displayText = !string.IsNullOrEmpty(title) ? title : lang.ToUpper();
                        if (!string.IsNullOrEmpty(details)) displayText += $" ({details.Trim()})";
                        
                        audioTracks.Add(new TrackItem
                        {
                            Id = i,
                            Text = displayText,
                            IsSelected = isSelected,
                            Type = "audio"
                        });
                    }
                }

                // Enumerate subtitle tracks
                var mfSubtitleTracks = playbackItem.TimedMetadataTracks;
                if (mfSubtitleTracks != null)
                {
                    for (int i = 0; i < mfSubtitleTracks.Count; i++)
                    {
                        var track = mfSubtitleTracks[i];
                        if (track.TimedMetadataKind == Windows.Media.Core.TimedMetadataKind.Caption || 
                            track.TimedMetadataKind == Windows.Media.Core.TimedMetadataKind.Subtitle)
                        {
                            string lang = track.Language ?? $"Track {i + 1}";
                            string title = track.Label ?? $"Track {i + 1}";
                            
                            bool isSelected = (mfSubtitleTracks.GetPresentationMode((uint)i) == Windows.Media.Playback.TimedMetadataTrackPresentationMode.PlatformPresented);
                            
                            newSubTracks.Add(new SubtitleTrackViewModel
                            {
                                Id = i,
                                Text = !string.IsNullOrEmpty(title) ? title : lang,
                                Lang = !string.IsNullOrEmpty(lang) ? lang : "und",
                                IsAddon = false,
                                IsSelected = isSelected
                            });
                        }
                    }
                }

                // Update Audio UI
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Native] Updating Audio UI...");
                if (_isPageLoaded && AudioListView != null)
                {
                    AudioListView.ItemsSource = audioTracks;
                    var selectedAudio = audioTracks.FirstOrDefault(t => t.IsSelected);
                    if (selectedAudio != null) AudioListView.SelectedItem = selectedAudio;
                }

                // Update Subtitle UI (Merge new tracks, keep addons)
                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Native] Updating Subtitle UI...");

                // Preserve addons
                var existingAddons = _currentSubtitleTracks.Where(t => t.IsAddon).ToList();
                _currentSubtitleTracks = newSubTracks;
                _currentSubtitleTracks.AddRange(existingAddons);

                if (_isPageLoaded)
                {
                    UpdateLanguageList(isLoading: true);
                }

                System.Diagnostics.Debug.WriteLine("[LoadTracksAsync_Native] Completed Successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync_Native] CRITICAL ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[LoadTracksAsync_Native] StackTrace: {ex.StackTrace}");
            }
            finally
            {
                _isPopulatingTracks = false;
            }
        }

        private async void AudioListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingTracks || !IsPlayerActive || AudioListView.SelectedItem is not TrackItem item) return;

            // Update models
            if (AudioListView.ItemsSource is IEnumerable<TrackItem> tracks)
            {
                foreach (var t in tracks) t.IsSelected = (t == item);
            }

            // Route to player-specific implementation
            if (_useMpvPlayer && _mpvPlayer != null)
            {
                await _mpvPlayer.SetPropertyAsync("aid", item.Id.ToString());
                ShowOsd($"Ses: {item.Text}");

                // SAVE PREFERENCE
                double duration = _mpvPlayer.Duration.TotalSeconds;
                HistoryManager.Instance.UpdateProgress(
                   _navArgs.Id ?? _navArgs.Title,
                   _navArgs.Title,
                   _streamUrl,
                   _mpvPlayer.Position.TotalSeconds,
                   duration,
                   aid: item.Id.ToString(), posterUrl: _navArgs.PosterUrl, type: _navArgs.Type, backdropUrl: _navArgs.BackdropUrl);
            }
            else if (!_useMpvPlayer && _nativeMediaPlayer != null)
            {
                SelectAudioTrack_Native((int)item.Id);
                ShowOsd($"Ses: {item.Text}");
            }
        }

        private void SelectAudioTrack_Native(int index)
        {
            if (_nativeMediaPlayer?.Source is not Windows.Media.Playback.MediaPlaybackItem playbackItem) return;

            try
            {
                // Use SelectedIndex property to switch audio tracks (no Select method exists)
                playbackItem.AudioTracks.SelectedIndex = index >= 0 ? index : 0;
                System.Diagnostics.Debug.WriteLine($"[SelectAudioTrack_Native] Selected audio track index: {playbackItem.AudioTracks.SelectedIndex}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SelectAudioTrack_Native] Error: {ex.Message}");
            }
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
            if (!IsPlayerActive || SubtitleLangListView.SelectedItem is not SubtitleLanguageViewModel lang || lang.IsLoadingItem) return;

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
            if (_isFetchingAddonSubs) return;
            _isFetchingAddonSubs = true;
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

                // [FIX] Improved Filename metadata logic
                // Using the raw stream ID from URL (e.g. 1594905.mkv) is useless for subtitle matching.
                // We construct a "virtual" filename based on the show's title for better accuracy.
                string displayTitle = _navArgs?.Title ?? (_navArgs?.SeriesName ?? "Video");
                string virtualFileName = displayTitle.Replace(":", "").Replace("/", "").Replace("\\", "");
                if (_navArgs?.Season > 0) virtualFileName += $".S{_navArgs.Season:D2}E{_navArgs.Episode:D2}";
                virtualFileName += ".mkv";
                extra = $"filename={Uri.EscapeDataString(virtualFileName)}";
                Debug.WriteLine($"[FetchAddonSubtitles] Using virtual filename for matching: {virtualFileName}");

                // 2. ID Resolution & Normalization (Crucial for subtitle addons)
                string queryId = imdbId;
                
                // [FIX] Resolve TMDB to IMDb if possible (Subtitle addons are IMDb-centric)
                if (imdbId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = imdbId.Split(':');
                    if (parts.Length > 1)
                    {
                        string tmdbIdOnly = parts[1];
                        string resolved = ModernIPTVPlayer.Services.Metadata.IdMappingService.Instance.GetImdbForTmdb(tmdbIdOnly);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            Debug.WriteLine($"[FetchAddonSubtitles] Resolved {imdbId} -> {resolved} via IdMappingService");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[FetchAddonSubtitles] IdMappingService MISS for TMDB:{tmdbIdOnly}");
                            // [NEW] Fallback: Check MetadataProvider's Cache directly (very likely to hit if arrived from MediaInfoPage)
                            var cached = ModernIPTVPlayer.Services.Metadata.MetadataProvider.Instance.TryPeekMetadata(new ModernIPTVPlayer.Models.Stremio.StremioMediaStream { Meta = new StremioMeta { Id = imdbId, Type = type } });
                            if (cached != null)
                            {
                                if (!string.IsNullOrEmpty(cached.ImdbId) && cached.ImdbId.StartsWith("tt"))
                                {
                                    resolved = cached.ImdbId;
                                    System.Diagnostics.Debug.WriteLine($"[FetchAddonSubtitles] Resolved {imdbId} -> {resolved} via MetadataProvider Cache");
                                }
                            }
                            else
                            {
                                AppLogger.Info($"[FetchAddonSubtitles] Cache PEEK MISS for {imdbId} (Type: {type})");
                            }
                        }

                        if (!string.IsNullOrEmpty(resolved))
                        {
                            imdbId = resolved;
                            queryId = resolved;
                        }
                    }
                }

                // [FIX] Suffix Handling: Avoid redundant :s:e if already present in ID
                if ((type == "series" || type == "episode") && !imdbId.Contains(":", StringComparison.Ordinal))
                {
                    int s = _navArgs?.Season > 0 ? _navArgs.Season : 1;
                    int e = _navArgs?.Episode > 0 ? _navArgs.Episode : 1;
                    queryId = $"{imdbId}:{s}:{e}";
                    type = "series"; 
                }
                else if (type == "series" || type == "episode")
                {
                    // Already has colons (e.g. tt1234567:1:1 or tmdb:79788:1:1), use as is
                    queryId = imdbId;
                    type = "series";
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
                                a.Manifest.Resources.Any(r => r.Name != null && r.Name.Contains("subtitles", StringComparison.OrdinalIgnoreCase)))
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
            finally
            {
                _isFetchingAddonSubs = false;
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

                // 1. Wait for player to be READY (IsMediaLoaded = true in mpv-core)
                // This indicates the player is ready to accept track commands (sub-add).
                Debug.WriteLine("[AutoSubRestore] Waiting for player to stabilize...");
                int subRestoreRetry = 0;
                while (_mpvPlayer != null && !_mpvPlayer.IsMediaLoaded && subRestoreRetry < 40)
                {
                    await Task.Delay(250); // 250ms x 40 = 10s max wait
                    subRestoreRetry++;
                    if (!_isPageLoaded) return;
                }
                
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
                SubtitleTrackViewModel match = null;

                if (history != null && !string.IsNullOrEmpty(history.SubtitleTrackUrl))
                {
                    match = _currentSubtitleTracks.FirstOrDefault(t => t.Url == history.SubtitleTrackUrl);
                }
                
                // If no history match, try PreferredSubtitleLanguage from Settings
                if (match == null && !string.IsNullOrEmpty(AppSettings.PlayerSettings.PreferredSubtitleLanguage))
                {
                    var prefs = AppSettings.PlayerSettings.PreferredSubtitleLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var p in prefs)
                    {
                        match = _currentSubtitleTracks.FirstOrDefault(t => t.IsAddon && (t.Lang.Equals(p, StringComparison.OrdinalIgnoreCase)));
                        if (match != null) break;
                    }
                }

                if (match != null)
                {
                    // 1. Mark as selected in model
                    foreach (var t in _currentSubtitleTracks) t.IsSelected = (t == match);
                    match.IsSelected = true;

                    // 2. Explicitly apply to MPV
                    if (_mpvPlayer != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[InjectAddonSubs] Auto-selecting subtitle: {match.Text} ({match.Url})");
                        _ = _mpvPlayer.ExecuteCommandAsync("sub-add", match.Url);
                        ShowOsd($"Altyazı Otomatik Seçildi: {match.Text}");
                    }

                    // 3. Sync UI Selection
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        SubtitleListView.SelectedItem = match;
                    });
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
            if (_isPopulatingTracks || !IsPlayerActive) return;
            if (SubtitleListView.SelectedItem is SubtitleTrackViewModel track)
            {
                // Update internal IsSelected state
                foreach (var t in _currentSubtitleTracks) t.IsSelected = (t == track);

                // Route to player-specific implementation
                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    if (track.IsAddon)
                    {
                        await _mpvPlayer.ExecuteCommandAsync("sub-add", track.Url);
                        ShowOsd($"Altyazı Yüklendi: {track.Text}");

                        // SAVE PREFERENCE
                        double duration = _mpvPlayer.Duration.TotalSeconds;
                        HistoryManager.Instance.UpdateProgress(
                           _navArgs.Id ?? _navArgs.Title,
                           _navArgs.Title,
                           _streamUrl,
                           _mpvPlayer.Position.TotalSeconds,
                            duration,
                            subUrl: track.Url,
                            posterUrl: _navArgs.PosterUrl,
                            type: _navArgs.Type,
                            backdropUrl: _navArgs.BackdropUrl);
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
                           _mpvPlayer.Position.TotalSeconds,
                            duration,
                            sid: track.Id.ToString(),
                            subUrl: null,
                            posterUrl: _navArgs.PosterUrl,
                            type: _navArgs.Type,
                            backdropUrl: _navArgs.BackdropUrl);
                    }
                }
                else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                {
                    SelectSubtitleTrack_Native(track.Id);
                    ShowOsd($"Altyazı: {track.Text}");
                }
            }
        }

        private void SelectSubtitleTrack_Native(int index)
        {
            if (_nativeMediaPlayer?.Source is not Windows.Media.Playback.MediaPlaybackItem playbackItem) return;
            
            try
            {
                var subtitleTracks = playbackItem.TimedMetadataTracks;
                if (index >= 0 && index < subtitleTracks.Count)
                {
                    // Enable the selected track
                    playbackItem.TimedMetadataTracks.SetPresentationMode(
                        (uint)index, 
                        Windows.Media.Playback.TimedMetadataTrackPresentationMode.PlatformPresented);
                    
                    // Disable other tracks
                    for (uint i = 0; i < subtitleTracks.Count; i++)
                    {
                        if (i != (uint)index)
                        {
                            playbackItem.TimedMetadataTracks.SetPresentationMode(
                                i, 
                                Windows.Media.Playback.TimedMetadataTrackPresentationMode.Disabled);
                        }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[SelectSubtitleTrack_Native] Selected track {index}");
                }
                else if (index < 0)
                {
                    // Disable all tracks (user selected "Off")
                    for (uint i = 0; i < subtitleTracks.Count; i++)
                    {
                        playbackItem.TimedMetadataTracks.SetPresentationMode(
                            i, 
                            Windows.Media.Playback.TimedMetadataTrackPresentationMode.Disabled);
                    }
                    System.Diagnostics.Debug.WriteLine($"[SelectSubtitleTrack_Native] Disabled all subtitles");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SelectSubtitleTrack_Native] Error: {ex.Message}");
            }
        }








        // ============================================================
        // STREAM URL RESOLUTION & VALIDATION (Optimized — non-blocking)
        // ============================================================

        /// <summary>
        /// Synchronous, cache-only URL resolver for internal protocols like iptv://.
        /// This replaces the blocking HEAD request that previously added ~575ms to startup.
        /// </summary>
        private string ResolveStreamUrl(string url)
        {
            // Basic cleanup: some servers dislike explicit :80
            url = url.Replace(":80/", "/");

            // Resolve internal iptv:// protocol from cache
            if (url.StartsWith("iptv://", StringComparison.OrdinalIgnoreCase))
            {
                string streamIdStr = url.Substring(7);
                if (int.TryParse(streamIdStr, out int streamId) && App.CurrentLogin != null)
                {
                    var playlistId = App.CurrentLogin.PlaylistUrl ?? "default";

                    // Try VOD first
                    var vods = ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod_streams").Result;
                    var match = vods?.FirstOrDefault(v => v.StreamId == streamId);
                    if (match != null)
                    {
                        string ext = match.ContainerExtension ?? "mkv";
                        if (!ext.StartsWith(".")) ext = "." + ext;
                        url = $"{App.CurrentLogin.Host}/movie/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{match.StreamId}{ext}";
                        LogPlayerTrace($"[PlayerPage] Resolved iptv://{streamId} to {url}");
                        return url;
                    }

                    // Try Live streams
                    var lives = ContentCacheService.Instance.LoadCacheAsync<LiveStream>(playlistId, "live_streams").Result;
                    var liveMatch = lives?.FirstOrDefault(l => l.StreamId == streamId);
                    if (liveMatch != null)
                    {
                        string ext = string.IsNullOrEmpty(liveMatch.ContainerExtension) ? "ts" : liveMatch.ContainerExtension;
                        url = $"{App.CurrentLogin.Host}/live/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{liveMatch.StreamId}.{ext}";
                        LogPlayerTrace($"[PlayerPage] Resolved iptv://{streamId} to live URL: {url}");
                        return url;
                    }
                }
            }

            return url;
        }

        /// <summary>
        /// Fire-and-forget stream validation. Sends a HEAD request for diagnostics
        /// and connection pre-warming, but NEVER blocks playback.
        /// Errors are logged; the proxy/MediaFoundation will handle real failures.
        /// </summary>
        private async Task ValidateStreamUrlInBackground(string url)
        {
            try
            {
                // Skip validation for known-trusted CDNs and local proxy URLs
                if (url.Contains("127.0.0.1") || url.Contains("localhost"))
                    return;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));

                using var response = await HttpHelper.Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

                var contentType = response.Content.Headers.ContentType?.MediaType;

                // Log diagnostics
                LogPlayerTrace($"[StreamValidation] HEAD response: {(int)response.StatusCode} {contentType ?? "unknown"}");

                // Warn on suspicious responses (webpage instead of video)
                if (contentType != null && contentType.StartsWith("text/") &&
                    !contentType.Contains("mpegurl") && !contentType.Contains("xml"))
                {
                    LogPlayerTrace("[StreamValidation] WARNING: Server returned text/html instead of video. Playback may fail.");
                }
            }
            catch (OperationCanceledException)
            {
                LogPlayerTrace("[StreamValidation] HEAD request timed out (non-fatal).");
            }
            catch (Exception ex)
            {
                LogPlayerTrace($"[StreamValidation] Error (non-fatal): {ex.Message}");
            }
        }

        private async Task<(bool Success, string Url, string ErrorMsg)> CheckStreamUrlAsync(string url)
        {
            LogStartupTiming("CheckStreamUrlAsync start");
            LogLaunchTiming("CheckStreamUrlAsync start");
            try
            {
                // [FIX] Resolve internal iptv:// protocol before checking or playing
                if (url.StartsWith("iptv://", StringComparison.OrdinalIgnoreCase))
                {
                    string streamIdStr = url.Substring(7);
                    if (int.TryParse(streamIdStr, out int streamId) && App.CurrentLogin != null)
                    {
                        var playlistId = App.CurrentLogin.PlaylistUrl ?? "default";
                        // Try VOD first
                        var vods = await ContentCacheService.Instance.LoadCacheAsync<VodStream>(playlistId, "vod_streams");
                        var match = vods?.FirstOrDefault(v => v.StreamId == streamId);
                        if (match != null)
                        {
                            string ext = match.ContainerExtension ?? "mkv";
                            if (!ext.StartsWith(".")) ext = "." + ext;
                            url = $"{App.CurrentLogin.Host}/movie/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{match.StreamId}{ext}";
                            Debug.WriteLine($"[PlayerPage] Resolved iptv://{streamId} to {url}");
                        }
                    }
                }

                // Basic cleanup: some servers dislike explicit :80
                var finalUrl = url.Replace(":80/", "/");

                using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                // Use a small range to avoid triggering "Download" limits if possible, 
                // but some servers hate Range. Let's try standard request headers only first.
                
                var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));

                try 
                {
                    // Use ResponseHeadersRead to avoid downloading the whole file.
                    // We wrap this in a using block to ensure the HttpResponseMessage is disposed 
                    // BEFORE MPV starts its own connection. This prevents connection leaks.
                    using var response = await HttpHelper.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    LogStartupTiming("CheckStreamUrlAsync response");
                    LogLaunchTiming("CheckStreamUrlAsync response");
                    
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
                    LogStartupTiming("CheckStreamUrlAsync done");
                    LogLaunchTiming("CheckStreamUrlAsync done");
                    return (true, finalUrl, string.Empty);
                }
                catch (Exception ex)
                {
                    return (false, finalUrl, $"Sunucuya bağlanılamadı: {ex.Message}");
                }
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
            // [PERF] High-frequency composition animations (Breathing/Glow) are 
            // the largest source of non-video GPU usage in WinUI 3.
            if (AppSettings.PlayerSettings.Profile == Models.PlayerProfile.Performance)
            {
                Debug.WriteLine("[PERF] Performance Mode: Skipping 'Alive System' UI animations.");
                return;
            }

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

        private bool AreUiAnimationsEnabled()
        {
            try
            {
                return _uiSettings.AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }

        private Windows.Graphics.RectInt32 GetCurrentWindowBounds(AppWindow appWindow)
        {
            return new Windows.Graphics.RectInt32(
                appWindow.Position.X,
                appWindow.Position.Y,
                appWindow.Size.Width,
                appWindow.Size.Height);
        }

        private Windows.Graphics.RectInt32 CalculatePiPTargetBounds(AppWindow appWindow)
        {
            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;

            int margin = Math.Max(16, Math.Min(28, workArea.Width / 72));
            int targetWidth = (int)Math.Round(workArea.Width * 0.24);
            targetWidth = Math.Clamp(targetWidth, 320, 520);

            int targetHeight = (int)Math.Round(targetWidth * 9.0 / 16.0);
            int maxHeight = Math.Max(180, workArea.Height - (margin * 2));
            if (targetHeight > maxHeight)
            {
                targetHeight = maxHeight;
                targetWidth = (int)Math.Round(targetHeight * 16.0 / 9.0);
            }

            int targetX = workArea.X + workArea.Width - targetWidth - margin;
            int targetY = workArea.Y + workArea.Height - targetHeight - margin;

            return new Windows.Graphics.RectInt32(targetX, targetY, targetWidth, targetHeight);
        }

        private Windows.Graphics.RectInt32 CalculateAspectFittedRestoreBounds(Windows.Graphics.RectInt32 savedBounds, double aspectRatio)
        {
            if (savedBounds.Width <= 0 || savedBounds.Height <= 0 || aspectRatio <= 0)
            {
                return savedBounds;
            }

            double targetWidth = savedBounds.Width;
            double targetHeight = targetWidth / aspectRatio;

            if (targetHeight > savedBounds.Height)
            {
                targetHeight = savedBounds.Height;
                targetWidth = targetHeight * aspectRatio;
            }

            int fittedWidth = Math.Max(1, (int)Math.Round(targetWidth));
            int fittedHeight = Math.Max(1, (int)Math.Round(targetHeight));
            int fittedX = savedBounds.X + ((savedBounds.Width - fittedWidth) / 2);
            int fittedY = savedBounds.Y + ((savedBounds.Height - fittedHeight) / 2);

            return new Windows.Graphics.RectInt32(fittedX, fittedY, fittedWidth, fittedHeight);
        }

        private bool IsMainWindowForeground()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(MainWindow.Current);
                return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
            }
            catch
            {
                return false;
            }
        }

        private bool IsPointerOverCurrentWindow(AppWindow appWindow)
        {
            try
            {
                if (!GetCursorPos(out POINT point))
                {
                    return false;
                }

                var bounds = GetCurrentWindowBounds(appWindow);
                return point.X >= bounds.X &&
                       point.X < bounds.X + bounds.Width &&
                       point.Y >= bounds.Y &&
                       point.Y < bounds.Y + bounds.Height;
            }
            catch
            {
                return false;
            }
        }

        private async Task WaitForAppWindowSteadyStateAsync(AppWindow appWindow, TimeSpan timeout)
        {
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<AppWindow, AppWindowChangedEventArgs>? handler = null;

            handler = (sender, args) =>
            {
                if (args.DidSizeChange || args.DidPositionChange)
                {
                    completion.TrySetResult(null);
                }
            };

            appWindow.Changed += handler;

            try
            {
                var completed = await Task.WhenAny(completion.Task, Task.Delay(timeout));
                if (completed != completion.Task)
                {
                    await Task.Yield();
                }
            }
            finally
            {
                appWindow.Changed -= handler;
            }
        }

        private async Task AnimateWindowBoundsAsync(AppWindow appWindow, Windows.Graphics.RectInt32 targetBounds, TimeSpan duration)
        {
            var startBounds = GetCurrentWindowBounds(appWindow);

            if (startBounds.X == targetBounds.X &&
                startBounds.Y == targetBounds.Y &&
                startBounds.Width == targetBounds.Width &&
                startBounds.Height == targetBounds.Height)
            {
                return;
            }

            if (!AreUiAnimationsEnabled() || duration <= TimeSpan.Zero)
            {
                appWindow.MoveAndResize(targetBounds);
                return;
            }

            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopwatch = Stopwatch.StartNew();
            EventHandler<object>? renderingHandler = null;
            Windows.Graphics.RectInt32? lastBounds = null;

            renderingHandler = (_, _) =>
            {
                double progress = stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds;
                float t = (float)Math.Clamp(progress, 0d, 1d);
                float eased = 1f - MathF.Pow(1f - t, 3f);

                int currentX = (int)MathF.Round(startBounds.X + ((targetBounds.X - startBounds.X) * eased));
                int currentY = (int)MathF.Round(startBounds.Y + ((targetBounds.Y - startBounds.Y) * eased));
                int currentWidth = (int)MathF.Round(startBounds.Width + ((targetBounds.Width - startBounds.Width) * eased));
                int currentHeight = (int)MathF.Round(startBounds.Height + ((targetBounds.Height - startBounds.Height) * eased));
                var currentBounds = new Windows.Graphics.RectInt32(currentX, currentY, currentWidth, currentHeight);

                if (lastBounds == null ||
                    lastBounds.Value.X != currentBounds.X ||
                    lastBounds.Value.Y != currentBounds.Y ||
                    lastBounds.Value.Width != currentBounds.Width ||
                    lastBounds.Value.Height != currentBounds.Height)
                {
                    appWindow.MoveAndResize(currentBounds);
                    lastBounds = currentBounds;
                }

                if (t >= 1f)
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= renderingHandler;
                    if (lastBounds == null ||
                        lastBounds.Value.X != targetBounds.X ||
                        lastBounds.Value.Y != targetBounds.Y ||
                        lastBounds.Value.Width != targetBounds.Width ||
                        lastBounds.Value.Height != targetBounds.Height)
                    {
                        appWindow.MoveAndResize(targetBounds);
                    }
                    completion.TrySetResult(null);
                }
            };

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += renderingHandler;
            await completion.Task;
        }

        private void EnsurePipOverlayAnimations()
        {
            if (_pipOverlayAnimationsInitialized)
            {
                UpdatePipOverlayCenterPoint();
                return;
            }

            ElementCompositionPreview.SetIsTranslationEnabled(PipControls, true);
            UpdatePipOverlayCenterPoint();
            PipControls.SizeChanged += (_, _) => UpdatePipOverlayCenterPoint();
            _pipOverlayAnimationsInitialized = true;
        }

        private void UpdatePipOverlayCenterPoint()
        {
            if (PipControls == null)
            {
                return;
            }

            var visual = ElementCompositionPreview.GetElementVisual(PipControls);
            float width = (float)Math.Max(1, PipControls.ActualWidth);
            float height = (float)Math.Max(1, PipControls.ActualHeight);
            visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0f);
        }

        private void SetPipOverlayImmediate(bool visible)
        {
            EnsurePipOverlayAnimations();
            _pipControlsVisible = visible;

            var visual = ElementCompositionPreview.GetElementVisual(PipControls);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.StopAnimation("Offset");

            float opacity = visible ? 1f : 0f;
            Vector3 scale = visible ? Vector3.One : new Vector3(0.985f, 0.985f, 1f);
            Vector3 offset = visible ? Vector3.Zero : new Vector3(0f, 10f, 0f);

            visual.Opacity = opacity;
            visual.Scale = scale;
            PipControls.Opacity = opacity;
            visual.Offset = offset;
        }

        private void AnimatePipOverlay(bool visible)
        {
            // Prevent redundant animation if already in target state
            if (_pipControlsVisible == visible) return;

            EnsurePipOverlayAnimations();
            _pipControlsVisible = visible;

            if (!AreUiAnimationsEnabled())
            {
                SetPipOverlayImmediate(visible);
                return;
            }

            var easing = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f),
                new Vector2(0.3f, 1f));

            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1f, visible ? 1f : 0f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(visible ? 160 : 120);

            var scale = _compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1f, visible ? new Vector3(1.01f, 1.01f, 1f) : new Vector3(0.985f, 0.985f, 1f), easing);
            scale.Duration = opacity.Duration;

            var offset = _compositor.CreateVector3KeyFrameAnimation();
            offset.InsertKeyFrame(1f, visible ? new Vector3(0f, -2f, 0f) : new Vector3(0f, 10f, 0f), easing);
            offset.Duration = opacity.Duration;

            var visual = ElementCompositionPreview.GetElementVisual(PipControls);
            visual.StartAnimation("Opacity", opacity);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Offset", offset);
        }

        private void PipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPiPTransitionRunning)
            {
                return;
            }

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
            if ((_useMpvPlayer && _mpvPlayer == null) || (!_useMpvPlayer && _nativeMediaPlayer == null) || _isPiPMode || _isPiPTransitionRunning) return;
            Debug.WriteLine("[PiP] Entering PiP Mode...");
            var appWindow = MainWindow.Current.AppWindow;
            
            try 
            {
                _isPiPTransitionRunning = true;

                // 1. Save Current Window State
                _savedWindowBounds = appWindow.Position.X != 0 || appWindow.Position.Y != 0 || appWindow.Size.Width != 0 ? 
                                     GetCurrentWindowBounds(appWindow) : 
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
                _savedPresenterKind = appWindow.Presenter.Kind;

                _isPiPMode = true;

                // 2. Hide UI Elements (Clean View)
                SetControlsVisibility(Visibility.Collapsed);
                OsdOverlay.Visibility = Visibility.Collapsed;

                _statsTimer.Stop();
                _cursorTimer.Stop();
                
                // [FIX] Restore cursor visibility for PiP mode
                if (_isCursorHidden)
                {
                    SetCursorVisible(true);
                }
                
                MainWindow.Current.SetTitleBarSuppressed(true);
                _pipControlsVisible = false;

                // Show PiP Controls Overlay
                PipOverlay.Visibility = Visibility.Visible;
                SetPipOverlayImmediate(false);

                // Sync Icons
                bool isPaused = false;
                string muteState = "no";
                
                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    isPaused = await _mpvPlayer.GetPropertyBoolAsync("pause");
                    muteState = await _mpvPlayer.GetPropertyAsync("mute");
                }
                else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                {
                    isPaused = _nativeMediaPlayer.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing;
                    muteState = _nativeMediaPlayer.IsMuted ? "yes" : "no";
                }

                PipPlayPauseIcon.Glyph = isPaused ? "\uF5B0" : "\uF8AE";
                // PiP mute icon shows the ACTION that will happen on click:
                // muted → show speaker (click to unmute)
                // unmuted → show cross (click to mute)
                PipMuteIcon.Glyph = (muteState == "yes") ? "\uE767" : "\uE74F";

                // Ensure we are not Fullscreen before transitioning
                if (_isFullScreen)
                {
                    MainWindow.Current.SetFullScreen(false);
                    _isFullScreen = false;
                    UpdateFullScreenUI();
                }

                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    // 3. Prepare for Transition
                    if (appWindow.Presenter is OverlappedPresenter presenter)
                    {
                        if (presenter.State == OverlappedPresenterState.Maximized)
                        {
                            presenter.Restore();
                            await Task.Delay(40);
                        }
                        // Make Always on Top
                        presenter.IsAlwaysOnTop = true;
                        presenter.SetBorderAndTitleBar(true, false);
                    }

                    // 4. Calculate Target Bounds (Bottom Right)
                    var targetBounds = CalculatePiPTargetBounds(appWindow);

                    // 5. Animate
                    _mpvPlayer.SuspendResize();
                    await AnimateWindowBoundsAsync(appWindow, targetBounds, TimeSpan.FromMilliseconds(220));

                    // 6. Finish
                    _mpvPlayer.ResumeResize();
                }
                else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                {
                    // NATIVE WINDOWS MEDIA FOUNDATION PiP (Now using OverlappedPresenter for Resizability)
                    if (appWindow.Presenter is OverlappedPresenter presenter)
                    {
                        presenter.Restore();
                        presenter.IsAlwaysOnTop = true;
                        presenter.SetBorderAndTitleBar(true, false);
                    }

                    var targetBounds = CalculatePiPTargetBounds(appWindow);
                    await AnimateWindowBoundsAsync(appWindow, targetBounds, TimeSpan.FromMilliseconds(220));
                }

                // [FIX] Removed SetTitleBar(PipDragRegion) - it was stealing pointer events
                // and preventing hover detection. Dragging is now handled manually via pointer events.

                // Hide System Caption Buttons (Min/Max/Close) by disabling system title bar
                if (appWindow.Presenter is OverlappedPresenter pipPresenter)
                {
                    pipPresenter.SetBorderAndTitleBar(true, false);
                }

                Debug.WriteLine("[PiP] Transition Complete");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PiP] Enter Error: {ex.Message}");
                _isPiPMode = false;

                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = false;
                    presenter.SetBorderAndTitleBar(true, true);
                }

                MainWindow.Current.SetTitleBarSuppressed(false);
                MainWindow.Current.SetTitleBar(MainWindow.Current.TitleBarElement);

                SetControlsVisibility(Visibility.Visible);

                _statsTimer.Start();
                ResumeAutoHideLifecycle(revealControls: IsMainWindowForeground() && IsPointerOverCurrentWindow(appWindow));

                PipOverlay.Visibility = Visibility.Collapsed;
                SetPipOverlayImmediate(false);

                if (_savedIsFullScreen)
                {
                    MainWindow.Current.SetFullScreen(true);
                    _isFullScreen = true;
                    UpdateFullScreenUI();
                }
            }
            finally
            {
                _isPiPTransitionRunning = false;
            }
        }

        private async void ExitPiP()
        {
            if (!_isPiPMode || _isPiPTransitionRunning) return;
            Debug.WriteLine("[PiP] Exiting PiP...");

            try
            {
                _isPiPTransitionRunning = true;
                _suppressPointerReveal = true;
                var appWindow = MainWindow.Current.AppWindow;
                AnimatePipOverlay(false);
                var restoreStageBounds = CalculateAspectFittedRestoreBounds(_savedWindowBounds, 16.0 / 9.0);

                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    // 1. Keep the video in a 16:9 path first, then restore shell bounds.
                    await AnimateWindowBoundsAsync(appWindow, restoreStageBounds, TimeSpan.FromMilliseconds(210));

                    if (restoreStageBounds.X != _savedWindowBounds.X ||
                        restoreStageBounds.Y != _savedWindowBounds.Y ||
                        restoreStageBounds.Width != _savedWindowBounds.Width ||
                        restoreStageBounds.Height != _savedWindowBounds.Height)
                    {
                        await AnimateWindowBoundsAsync(appWindow, _savedWindowBounds, TimeSpan.FromMilliseconds(110));
                    }
                }
                else if (!_useMpvPlayer && _nativeMediaPlayer != null)
                {
                    // NATIVE EXIT PIP - Keep exit content-led before exact restore.
                    await AnimateWindowBoundsAsync(appWindow, restoreStageBounds, TimeSpan.FromMilliseconds(210));

                    if (restoreStageBounds.X != _savedWindowBounds.X ||
                        restoreStageBounds.Y != _savedWindowBounds.Y ||
                        restoreStageBounds.Width != _savedWindowBounds.Width ||
                        restoreStageBounds.Height != _savedWindowBounds.Height)
                    {
                        await AnimateWindowBoundsAsync(appWindow, _savedWindowBounds, TimeSpan.FromMilliseconds(110));
                    }
                }

                if (!_useMpvPlayer)
                {
                    // For Native/MF, we set it back to Default now that we've finished the animation
                    appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
                }

                // 2. Restore Window State
                _isPiPMode = false;

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

                await WaitForAppWindowSteadyStateAsync(appWindow, TimeSpan.FromMilliseconds(120));
                await Task.Yield();
                await Task.Delay(16);

                if (_useMpvPlayer && _mpvPlayer != null)
                {
                    _mpvPlayer.ResumeResize();
                }

                MainWindow.Current.SetTitleBarSuppressed(false);
                MainWindow.Current.SetTitleBar(MainWindow.Current.TitleBarElement);

                // 3. Show UI
                _statsTimer.Start();

                bool shouldRevealControls = IsMainWindowForeground() && IsPointerOverCurrentWindow(appWindow);
                SetControlsVisibility(shouldRevealControls ? Visibility.Visible : Visibility.Collapsed);
                ResumeAutoHideLifecycle(revealControls: shouldRevealControls);

                // Hide PiP Controls Overlay
                PipOverlay.Visibility = Visibility.Collapsed;
                SetPipOverlayImmediate(false);

                Debug.WriteLine("[PiP] Restored Normal View");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PiP] Exit Error: {ex.Message}");
                // Force state reset
                _isPiPMode = false;
                MainWindow.Current.SetTitleBarSuppressed(false);
                MainWindow.Current.SetTitleBar(MainWindow.Current.TitleBarElement);
                SetControlsVisibility(Visibility.Collapsed);
                MainWindow.Current.Activate();
            }
            finally
            {
                _isPiPTransitionRunning = false;
                // Re-enable pointer events after a short delay to let the window settle
                this.DispatcherQueue.TryEnqueue(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(300);
                    _suppressPointerReveal = false;
                });
            }
        }



        private async void TogglePiPAsync()
        {
            PipButton_Click(null, null);
        }



        private void PipOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPiPMode) return;

            var properties = e.GetCurrentPoint(PipOverlay).Properties;
            if (properties.IsLeftButtonPressed)
            {
                _isPiPDragging = true;
                PipOverlay.CapturePointer(e.Pointer);
                _pipDragLastPosition = GetCursorScreenPosition();
            }
        }

        private void PipOverlay_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPiPMode) return;
        }

        private void PipOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPiPMode) return;

            if (_isPiPDragging)
            {
                var currentPosition = GetCursorScreenPosition();
                int deltaX = currentPosition.X - _pipDragLastPosition.X;
                int deltaY = currentPosition.Y - _pipDragLastPosition.Y;

                var appWindow = MainWindow.Current.AppWindow;
                var windowPos = appWindow.Position;
                appWindow.Move(new Windows.Graphics.PointInt32(windowPos.X + deltaX, windowPos.Y + deltaY));

                _pipDragLastPosition = currentPosition;
                return;
            }

            if (_pipControlsVisible) return;
            AnimatePipOverlay(true);
        }

        private void PipOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPiPMode) return;
            _isPiPDragging = false;
            PipOverlay.ReleasePointerCaptures();
        }

        private void PipOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPiPMode || _isPiPDragging) return;
            AnimatePipOverlay(false);
        }

        private Windows.Graphics.PointInt32 GetCursorScreenPosition()
        {
            GetCursorPos(out POINT p);
            return new Windows.Graphics.PointInt32(p.X, p.Y);
        }
        private void OnMpvPropertyChanged(object? sender, Mpv.Core.Structs.Client.MpvEventProperty e)
        {
            if (!IsPlayerActive) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                switch (e.Name)
                {
                    case "pause":
                        bool isPaused = Marshal.ReadInt32(e.DataPtr) == 1;
                        _isPaused = isPaused;
                        PlayPauseIcon.Glyph = isPaused ? "\uF5B0" : "\uF8AE";
                        if (PipPlayPauseIcon != null) PipPlayPauseIcon.Glyph = isPaused ? "\uF5B0" : "\uF8AE";
                        
                        if (isPaused)
                        {
                            if (!_inactivityTimer.IsEnabled && !_isInactivityOverlayVisible)
                            {
                                _pauseStartTime = DateTime.Now;
                                _inactivityTimer.Start();
                            }
                        }
                        else
                        {
                            _inactivityTimer.Stop();
                            if (_isInactivityOverlayVisible) HideInactivityOverlay();
                        }
                        break;
                    case "demuxer-cache-duration":
                        double cacheDur = 0;
                        if (e.Format == Mpv.Core.Enums.Client.MpvFormat.Double)
                        {
                             cacheDur = (double)Marshal.PtrToStructure(e.DataPtr, typeof(double));
                        }
                        TxtBuffer.Text = $"{cacheDur:F1}s";
                        break;
                    case "hwdec-current":
                        string? hwdec = Marshal.PtrToStringUTF8(e.DataPtr);
                        TxtHardware.Text = hwdec ?? "no";
                        break;
                    case "frame-drop-count":
                        long drops = Marshal.ReadInt64(e.DataPtr);
                        TxtDroppedDecoder.Text = drops.ToString();
                        break;
                }
            });
        }
    }
}
