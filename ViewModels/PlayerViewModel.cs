using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Playback;

namespace ModernIPTVPlayer.ViewModels
{
    /// <summary>
    /// ViewModel for the PlayerPage, managing playback state and UI synchronization.
    /// </summary>
    public enum TimeDisplayMode
    {
        Standard,   // 01:20 / 45:00
        Remaining,  // 01:20 / -43:40
        Percent     // 01:20 (3%)
    }

    public class BadgeViewModel
    {
        public string Text { get; set; }
        public Microsoft.UI.Xaml.Media.Brush ColorBrush { get; set; }
        public bool IsGradient { get; set; }
    }

    public class PlayerViewModel : BaseViewModel, IDisposable
    {
        private readonly DispatcherQueue _dispatcher;
        private IPlaybackEngine? _engine;
        
        private string _title = string.Empty;
        private PlaybackState _state = PlaybackState.Idle;
        private double _positionSeconds;
        private double _durationSeconds;
        private string _timeText = "00:00 / 00:00";
        private double _volume = 100;
        private double _speed = 1.0;
        private bool _isMuted;
        private bool _isLive;
        private TimeDisplayMode _currentTimeDisplayMode = TimeDisplayMode.Standard;
        private bool _isBuffering;
        private string _error = string.Empty;
        private DateTime _lastTimeTextUpdate = DateTime.MinValue;

        // UI State
        private bool _areControlsVisible = true;
        private bool _isInactivityOverlayVisible;
        private DispatcherQueueTimer? _controlHideTimer;
        private DispatcherQueueTimer? _inactivityOverlayTimer;
        private DateTime _pauseStartTime;
        private bool _isInitializing;
        private DateTime _lastMetadataUpdate = DateTime.MinValue;

        // Technical Stats
        private string _hardwareDecoding = "no";
        private string _bufferDuration = "-";
        private string _droppedFrames = "0";

        private string _renderer = "-";
        private string _avSync = "-";

        // Technical Metadata
        private string _resolution = "-";
        private string _fps = "-";
        private string _videoCodec = "-";
        private string _audioCodec = "-";
        private string _bitrate = "-";
        private bool _isHdr;
        private string _sdrWhite = "-";
        private string _displayPeak = "-";
        private string _colorspace = "-";
        private string _primaries = "-";
        private string _audioChannels = "-";
        private string _downloadSpeed = "-";
        private string _targetSdrWhite = "-";
        private string _appliedPeak = "-";
        private bool _hdrAvailable = false;
        private string _osdText = "";
        private bool _isOsdVisible;
        private System.Threading.CancellationTokenSource? _osdCts;

        // Inactivity Metadata
        private string _inactivityTitle = "";
        private string _inactivityDescription = "";
        private Microsoft.UI.Xaml.Media.ImageSource? _inactivityLogoUrl;
        private bool _hasInactivityLogo;
        private string _inactivityYear = "";
        private string _inactivityDuration = "";
        private string _inactivityRating = "";
        private string _inactivityRemainingText = "";
        private string _inactivityChapterInfo = "";
        private System.Collections.ObjectModel.ObservableCollection<BadgeViewModel> _inactivityBadges = new();

        /// <summary>
        /// Gets or sets the media title.
        /// </summary>
        public string Title { get => _title; set => SetProperty(ref _title, value); }

        /// <summary>
        /// Gets the current playback state.
        /// </summary>
        public PlaybackState State { get => _state; private set => SetProperty(ref _state, value); }

        /// <summary>
        /// Gets the formatted time text (e.g., "01:23 / 10:00").
        /// </summary>
        public string TimeText { get => _timeText; private set => SetProperty(ref _timeText, value); }

        public string InactivityTitle { get => _inactivityTitle; set => SetProperty(ref _inactivityTitle, value); }
        public string InactivityDescription { get => _inactivityDescription; set => SetProperty(ref _inactivityDescription, value); }
        public Microsoft.UI.Xaml.Media.ImageSource? InactivityLogoUrl { get => _inactivityLogoUrl; set => SetProperty(ref _inactivityLogoUrl, value); }
        public bool HasInactivityLogo { get => _hasInactivityLogo; set => SetProperty(ref _hasInactivityLogo, value); }
        public string InactivityYear { get => _inactivityYear; set => SetProperty(ref _inactivityYear, value); }
        public string InactivityDuration { get => _inactivityDuration; set => SetProperty(ref _inactivityDuration, value); }
        public string InactivityRating { get => _inactivityRating; set => SetProperty(ref _inactivityRating, value); }
        public string InactivityRemainingText { get => _inactivityRemainingText; set => SetProperty(ref _inactivityRemainingText, value); }
        public string InactivityChapterInfo { get => _inactivityChapterInfo; set => SetProperty(ref _inactivityChapterInfo, value); }
        public System.Collections.ObjectModel.ObservableCollection<BadgeViewModel> InactivityBadges { get => _inactivityBadges; set => SetProperty(ref _inactivityBadges, value); }

        /// <summary>
        /// Gets the current playback position in seconds (for Slider binding).
        /// </summary>
        public double PositionSeconds 
        { 
            get => _positionSeconds; 
            private set 
            {
                // [ROOT FIX] Clamp position to duration to prevent WinUI Slider E_UNEXPECTED crash
                double safeMax = DurationSeconds > 0 ? DurationSeconds : 0.001;
                double clampedValue = Math.Max(0, Math.Min(value, safeMax));
                
                if (SetProperty(ref _positionSeconds, clampedValue))
                {
                    UpdateTimeText();
                }
            }
        }

        /// <summary>
        /// Gets the total duration in seconds (for Slider binding).
        /// </summary>
        public double DurationSeconds 
        { 
            get => _durationSeconds; 
            private set 
            {
                if (SetProperty(ref _durationSeconds, value))
                {
                    Debug.WriteLine($"[VM] DurationSeconds updated: {value:F3}");
                    UpdateTimeText();
                    OnPropertyChanged(nameof(IsTimelineAvailable));
                }
            }
        }

        /// <summary>
        /// Gets whether the player is actively playing.
        /// </summary>
        public bool IsPlaying => State == PlaybackState.Playing;

        /// <summary>
        /// Gets whether the player is paused.
        /// </summary>
        public bool IsPaused => State == PlaybackState.Paused;

        /// <summary>
        /// Gets the Play/Pause icon glyph based on state.
        /// </summary>
        public string PlayPauseIcon => IsPlaying ? "\uF8AE" : "\uF5B0";

        /// <summary>
        /// Gets whether the timeline (Seek bar) should be visible/active.
        /// </summary>
        public bool IsTimelineAvailable 
        {
            get
            {
                bool available = (State == PlaybackState.Playing || State == PlaybackState.Paused || State == PlaybackState.Buffering || State == PlaybackState.Seeking || State == PlaybackState.Idle) && DurationSeconds > 0.1;
                if (!available)
                {
                    Debug.WriteLine($"[VM] IsTimelineAvailable FALSE: State={State}, Dur={DurationSeconds:F3}");
                }
                return available;
            }
        }

        public TimeDisplayMode CurrentTimeDisplayMode 
        { 
            get => _currentTimeDisplayMode; 
            set 
            {
                if (SetProperty(ref _currentTimeDisplayMode, value))
                {
                    UpdateTimeText(true); // Bypass throttle on mode change
                }
            }
        }

        /// <summary>
        /// Gets or sets the playback volume (0.0 to 100.0).
        /// </summary>
        public double Volume 
        { 
            get => _volume; 
            set 
            {
                if (SetProperty(ref _volume, value) && _engine != null && !_isInitializing)
                    _engine.Volume = value;
            }
        }

        public double Speed
        {
            get => _speed;
            set
            {
                if (SetProperty(ref _speed, value) && _engine != null && !_isInitializing)
                    _engine.Speed = value;
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetProperty(ref _isMuted, value) && _engine != null && !_isInitializing)
                    _engine.IsMuted = value;
            }
        }

        public bool IsLive 
        { 
            get => _isLive; 
            set 
            {
                if (SetProperty(ref _isLive, value))
                {
                    UpdateTimeText();
                }
            }
        }

        public bool IsBuffering { get => _isBuffering; set => SetProperty(ref _isBuffering, value); }

        /// <summary>
        /// Gets the latest error message.
        /// </summary>
        public string Error { get => _error; private set => SetProperty(ref _error, value); }

        // Metadata Properties
        public string Resolution { get => _resolution; set => SetProperty(ref _resolution, value); }
        public string Fps { get => _fps; set => SetProperty(ref _fps, value); }
        public string VideoCodec { get => _videoCodec; set => SetProperty(ref _videoCodec, value); }
        public string AudioCodec { get => _audioCodec; set => SetProperty(ref _audioCodec, value); }
        public string Bitrate { get => _bitrate; set => SetProperty(ref _bitrate, value); }
        public bool IsHdr { get => _isHdr; set => SetProperty(ref _isHdr, value); }
        public string SdrWhite { get => _sdrWhite; set => SetProperty(ref _sdrWhite, value); }
        public string DisplayPeak { get => _displayPeak; set => SetProperty(ref _displayPeak, value); }
        
        public bool IsDisplayPeakVisible => !string.IsNullOrEmpty(DisplayPeak) && DisplayPeak != "-";
        public bool IsSdrWhiteVisible => !string.IsNullOrEmpty(SdrWhite) && SdrWhite != "-";
        
        public string Colorspace { get => _colorspace; set => SetProperty(ref _colorspace, value); }
        public string Primaries { get => _primaries; set => SetProperty(ref _primaries, value); }
        public string AudioChannels { get => _audioChannels; set => SetProperty(ref _audioChannels, value); }
        public string DownloadSpeed { get => _downloadSpeed; set => SetProperty(ref _downloadSpeed, value); }
        public string TargetSdrWhite { get => _targetSdrWhite; set => SetProperty(ref _targetSdrWhite, value); }
        public string AppliedPeak { get => _appliedPeak; set => SetProperty(ref _appliedPeak, value); }
        public bool HdrAvailable { get => _hdrAvailable; set => SetProperty(ref _hdrAvailable, value); }
        public string OsdText { get => _osdText; set => SetProperty(ref _osdText, value); }
        public bool IsOsdVisible { get => _isOsdVisible; set => SetProperty(ref _isOsdVisible, value); }

        /// <summary>
        /// Gets or sets whether the playback controls are currently visible.
        /// </summary>
        public bool AreControlsVisible { get => _areControlsVisible; set => SetProperty(ref _areControlsVisible, value); }

        /// <summary>
        /// Gets or sets whether the inactivity (dimmed) overlay is visible.
        /// </summary>
        public bool IsInactivityOverlayVisible 
        { 
            get => _isInactivityOverlayVisible; 
            set 
            {
                if (SetProperty(ref _isInactivityOverlayVisible, value) && value)
                {
                    UpdateInactivityBadges();
                }
            }
        }

        public string HardwareDecoding { get => _hardwareDecoding; set => SetProperty(ref _hardwareDecoding, value); }
        public string BufferDuration { get => _bufferDuration; set => SetProperty(ref _bufferDuration, value); }
        public string DroppedFrames { get => _droppedFrames; set => SetProperty(ref _droppedFrames, value); }
        public string Renderer { get => _renderer; set => SetProperty(ref _renderer, value); }
        public string AvSync { get => _avSync; set => SetProperty(ref _avSync, value); }

        public PlayerViewModel(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
            InitializeTimers();
        }

        /// <summary>
        /// Pushes position/duration from timer polling (safe async MPV API) into the ViewModel.
        /// Called from PlayerUpdateTimer_Tick to avoid COM interop exceptions.
        /// </summary>
        public void SyncPlaybackPosition(double position, double duration)
        {
            // [ROBUST SYNC] Update duration and position with individual try-catches
            // to ensure one property binding failure (e.g. Slider COMException) doesn't stop the rest.
            
            if (Math.Abs(DurationSeconds - duration) > 0.1)
            {
                if (SetProperty(ref _durationSeconds, duration))
                {
                    Debug.WriteLine($"[VM] Sync: Duration updated to {duration:F3}");
                    OnPropertyChanged(nameof(IsTimelineAvailable));
                }
            }

            if (Math.Abs(_positionSeconds - position) > 0.1)
            {
                PositionSeconds = position;
                // PositionSeconds setter already calls UpdateTimeText
            }
            
            // Periodically refresh availability just in case
            OnPropertyChanged(nameof(IsTimelineAvailable));

            // Always ensure time text is updated even if property setters threw exceptions
            UpdateTimeText();
        }

        private void InitializeTimers()
        {
            _controlHideTimer = _dispatcher.CreateTimer();
            _controlHideTimer.Interval = TimeSpan.FromSeconds(3);
            _controlHideTimer.Tick += (s, e) => 
            {
                _controlHideTimer.Stop();
                AreControlsVisible = false;
            };

            _inactivityOverlayTimer = _dispatcher.CreateTimer();
            _inactivityOverlayTimer.Interval = TimeSpan.FromSeconds(1);
            _inactivityOverlayTimer.Tick += (s, e) => 
            {
                if (State == PlaybackState.Paused)
                {
                    var elapsed = (DateTime.Now - _pauseStartTime).TotalSeconds;
                    if (elapsed >= 3)
                    {
                        if (!IsInactivityOverlayVisible)
                        {
                            IsInactivityOverlayVisible = true;
                            AreControlsVisible = false;
                        }
                        
                        // Senior Refinement: Update live countdown on the overlay
                        UpdateInactivityCountdown();
                    }
                }
            };
        }

        private void UpdateInactivityCountdown()
        {
            double rem = DurationSeconds - PositionSeconds;
            if (rem < 0) rem = 0;
            TimeSpan t = TimeSpan.FromSeconds(Math.Round(rem));
            string fmt = t.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss";
            InactivityRemainingText = t.ToString(fmt) + " kaldı";
        }

        private Microsoft.UI.Xaml.Media.Brush GetBrush(string color)
        {
            if (color == "GoldGradient") return (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["GoldGradient"];
            if (color == "SilverGradient") return (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["SilverGradient"];
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(AppColorHelper.ToWindowsColor(color));
        }

        private void UpdateInactivityBadges()
        {
            InactivityBadges.Clear();

            // 1. HDR/SDR Badge
            if (!string.IsNullOrEmpty(Colorspace) && Colorspace != "-")
            {
                bool isHdr = IsHdrContent(Colorspace) || _isHdr;
                InactivityBadges.Add(new BadgeViewModel 
                { 
                    Text = isHdr ? "HDR" : "SDR", 
                    ColorBrush = GetBrush(isHdr ? "SilverGradient" : "#66FFFFFF"), 
                    IsGradient = isHdr 
                });
            }

            // 2. Resolution Badge
            if (!string.IsNullOrEmpty(Resolution) && Resolution != "-")
            {
                bool is4K = Resolution.Contains("3840") || Resolution.Contains("4K") || Resolution.Contains("2160");
                if (is4K)
                {
                    InactivityBadges.Add(new BadgeViewModel 
                    { 
                        Text = "4K UHD", 
                        ColorBrush = GetBrush("GoldGradient"), 
                        IsGradient = true 
                    });
                }
                else
                {
                    string displayRes = Resolution.Contains("x") ? Resolution.Split('x')[1] + "P" : Resolution;
                    InactivityBadges.Add(new BadgeViewModel 
                    { 
                        Text = displayRes.ToUpperInvariant(), 
                        ColorBrush = GetBrush("#66FFFFFF"), 
                        IsGradient = false 
                    });
                }
            }

            // 3. Codec Badge
            if (!string.IsNullOrEmpty(VideoCodec) && VideoCodec != "-")
            {
                string codec = VideoCodec.ToUpperInvariant();
                if (codec.Contains("HIGH EFFICIENCY VIDEO CODING") || codec.Contains("HEVC")) codec = "HEVC";
                else if (codec.Contains("H264") || codec.Contains("AVC")) codec = "H.264";
                else if (codec.Contains("H265")) codec = "H.265";
                else if (codec.Contains("VP9")) codec = "VP9";
                else if (codec.Contains("AV1")) codec = "AV1";

                InactivityBadges.Add(new BadgeViewModel 
                { 
                    Text = codec, 
                    ColorBrush = GetBrush("#66FFFFFF"), 
                    IsGradient = false 
                });
            }
        }

        private bool IsHdrContent(string val)
        {
            return val.Contains("HDR", StringComparison.OrdinalIgnoreCase) || 
                   val.Contains("PQ", StringComparison.OrdinalIgnoreCase) || 
                   val.Contains("HLG", StringComparison.OrdinalIgnoreCase) ||
                   val.Contains("WCG", StringComparison.OrdinalIgnoreCase) ||
                   val.Contains("BT.2020", StringComparison.OrdinalIgnoreCase) ||
                   val.Contains("P3", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Notifies the ViewModel that a user interaction occurred.
        /// Resets the control hide timer and ensures controls are visible.
        /// </summary>
        public void OnUserInteraction()
        {
            AreControlsVisible = true;
            if (IsInactivityOverlayVisible)
            {
                IsInactivityOverlayVisible = false;
                // Resume pause timer if still paused
                if (State == PlaybackState.Paused)
                {
                    _pauseStartTime = DateTime.Now;
                    _inactivityOverlayTimer?.Start();
                }
            }

            _controlHideTimer?.Stop();
            _controlHideTimer?.Start();
        }

        /// <summary>
        /// Signals that loading has started (called by View on navigation).
        /// </summary>
        public void OnLoadingStarted() => IsBuffering = true;

        /// <summary>
        /// Signals that loading has finished (called by View after fade-out completes).
        /// </summary>
        public void OnLoadingFinished() => IsBuffering = false;

        public void ShowOsd(string text)
        {
            _osdCts?.Cancel();
            _osdCts = new System.Threading.CancellationTokenSource();
            
            OsdText = text;
            IsOsdVisible = true;
            
            var token = _osdCts.Token;
            Task.Delay(2000, token).ContinueWith(t => 
            {
                if (!t.IsCanceled)
                {
                    _dispatcher.TryEnqueue(() => IsOsdVisible = false);
                }
            });
        }

        /// <summary>
        /// Attaches a playback engine to the ViewModel.
        /// </summary>
        public void AttachEngine(IPlaybackEngine? engine)
        {
            if (_engine != null) DetachEngine();

            _engine = engine;
            if (_engine == null) return;

            _engine.StateChanged += OnEngineStateChanged;
            _engine.DurationChanged += OnEngineDurationChanged;
            _engine.ErrorOccurred += OnEngineError;
            _engine.MetadataChanged += OnEngineMetadataChanged;
            _engine.BufferingChanged += OnEngineBufferingChanged;
            _engine.SeekingChanged += OnEngineSeekingChanged;

            // Sync initial state (Restored with Guard)
            _isInitializing = true;
            try
            {
                State = _engine.State;
                Volume = _engine.Volume;
                Speed = _engine.Speed;
                IsMuted = _engine.IsMuted;
                DurationSeconds = _engine.Duration.TotalSeconds;
                PositionSeconds = _engine.Position.TotalSeconds;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void DetachEngine()
        {
            if (_engine != null)
            {
                _engine.MetadataChanged -= OnEngineMetadataChanged;
                _engine.StateChanged -= OnEngineStateChanged;
                _engine.DurationChanged -= OnEngineDurationChanged;
                _engine.BufferingChanged -= OnEngineBufferingChanged;
                _engine.SeekingChanged -= OnEngineSeekingChanged;
                _engine.Dispose();
            }
            _engine = null;
        }

        private void OnEngineStateChanged(object? sender, PlaybackState state)
        {
            OnEngineStateChanged(state);
        }

        public void OnEngineStateChanged(PlaybackState state)
        {
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    Debug.WriteLine($"[VM] State changing: {State} -> {state}");
                    State = state;
                    OnPropertyChanged(nameof(IsPlaying));
                    OnPropertyChanged(nameof(IsTimelineAvailable));
                    
                    if (state == PlaybackState.Buffering || state == PlaybackState.Opening || state == PlaybackState.Seeking)
                        IsBuffering = true;
                    else if (state == PlaybackState.Error || state == PlaybackState.Ended)
                        IsBuffering = false;
                    
                    if (state == PlaybackState.Playing)
                    {
                        IsInactivityOverlayVisible = false;
                        _inactivityOverlayTimer?.Stop();
                        OnUserInteraction();
                    }
                    else if (state == PlaybackState.Paused)
                    {
                        _pauseStartTime = DateTime.Now;
                        _inactivityOverlayTimer?.Start();
                    }

                    OnPropertyChanged(nameof(IsPaused));
                    OnPropertyChanged(nameof(PlayPauseIcon));
                }
                catch (Exception)
                {
                }
            });
        }

        private void OnEngineDurationChanged(object? sender, TimeSpan duration)
        {
            Debug.WriteLine("[VM_TRACE_DUR_H] Received");
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VM] Duration Changed: {duration}");
                    DurationSeconds = duration.TotalSeconds;
                    UpdateTimeText();
                    OnPropertyChanged(nameof(IsTimelineAvailable));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VM_CRASH] Error in OnEngineDurationChanged: {ex}");
                }
            });
        }

        private void OnEngineError(object? sender, string message)
        {
            _dispatcher.TryEnqueue(() =>
            {
                Error = message;
                State = PlaybackState.Error;
            });
        }

        private void OnEngineBufferingChanged(object? sender, bool isBuffering)
        {
            // Engine already dispatches to UI thread
            if (isBuffering && State != PlaybackState.Error)
                IsBuffering = true;
        }

        private void OnEngineSeekingChanged(object? sender, bool isSeeking)
        {
            // Reserved for future use
        }

        private void OnEngineMetadataChanged(object? sender, EventArgs e)
        {
            // Throttle metadata updates to at most 2 times per second to prevent UI lag
            if ((DateTime.Now - _lastMetadataUpdate).TotalMilliseconds < 500) return;

            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (_engine == null) return;
                    _lastMetadataUpdate = DateTime.Now;

                    Resolution = _engine.Resolution;
                    Fps = _engine.Fps;
                    VideoCodec = _engine.VideoCodec;
                    AudioCodec = _engine.AudioCodec;
                    Bitrate = _engine.Bitrate;
                    HardwareDecoding = _engine.HardwareDecoding;
                    Renderer = _engine.Renderer;
                    BufferDuration = _engine.BufferDuration;
                    AvSync = _engine.AvSync;
                    DroppedFrames = _engine.DroppedFrames;

                    Colorspace = _engine.Colorspace;
                    Primaries = _engine.Primaries;
                    AudioChannels = _engine.AudioChannels;
                    IsHdr = _engine.IsHdr;
                    SdrWhite = _engine.SdrWhite;
                    DisplayPeak = _engine.DisplayPeak;
                    
                    OnPropertyChanged(nameof(IsDisplayPeakVisible));
                    OnPropertyChanged(nameof(IsSdrWhiteVisible));
                    
                    DownloadSpeed = _engine.DownloadSpeed;
                    TargetSdrWhite = _engine.TargetSdrWhite;
                    AppliedPeak = _engine.AppliedPeak;
                    Renderer = _engine.Renderer;
                    
                    HdrAvailable = _engine.HdrAvailable;
                    
                    MetadataChanged?.Invoke(this, EventArgs.Empty);
                    OnPropertyChanged(string.Empty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VM_ERROR] Error in OnEngineMetadataChanged: {ex}");
                }
            });
        }

        public event EventHandler? MetadataChanged;

        private void UpdateTimeText(bool bypassThrottle = false)
        {
            // Throttle UI text updates to avoid excessive layout passes, but ensure accuracy
            if (!bypassThrottle && (DateTime.Now - _lastTimeTextUpdate).TotalMilliseconds < 200 && !IsLive) return;
            _lastTimeTextUpdate = DateTime.Now;

            try
            {
                if (IsLive)
                {
                    if (TimeText != "LIVE") TimeText = "LIVE";
                    return;
                }

                // Grid-lock to whole seconds to prevent "jitter" between elapsed and remaining times
                long roundedPos = (long)Math.Round(PositionSeconds);
                long roundedDur = (long)Math.Round(DurationSeconds);
                
                TimeSpan pos = TimeSpan.FromSeconds(roundedPos);
                TimeSpan dur = TimeSpan.FromSeconds(roundedDur);
                
                // Format based on duration
                string fmt = dur.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss";
                
                string newText;
                switch (_currentTimeDisplayMode)
                {
                    case TimeDisplayMode.Remaining:
                        long roundedRem = roundedDur - roundedPos;
                        if (roundedRem < 0) roundedRem = 0;
                        TimeSpan rem = TimeSpan.FromSeconds(roundedRem);
                        newText = $"{pos.ToString(fmt)} / -{rem.ToString(fmt)}";
                        break;
                    case TimeDisplayMode.Percent:
                        double pct = roundedDur > 0 ? Math.Clamp((double)roundedPos / roundedDur * 100.0, 0, 100) : 0;
                        newText = $"{pos.ToString(fmt)} ({pct:F0}%)";
                        break;
                    case TimeDisplayMode.Standard:
                    default:
                        newText = $"{pos.ToString(fmt)} / {dur.ToString(fmt)}";
                        break;
                }

                if (TimeText != newText)
                {
                    TimeText = newText;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM_ERROR] UpdateTimeText failure: {ex.Message}");
            }
        }

        protected override void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            try
            {
                base.OnPropertyChanged(propertyName);
            }
            catch (Exception ex)
            {
                // This catch handles any other unexpected binding errors (e.g. TextBlock, Buttons)
                // ensuring the main synchronization loop continues even if a UI element fails.
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [VM_WND] PropertyChanged exception for '{propertyName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Toggles between play and pause.
        /// </summary>
        public void TogglePlayPause()
        {
            if (_engine == null) return;
            if (State == PlaybackState.Playing) _engine.Pause();
            else _engine.Play();
        }

        public void SeekRelative(int seconds)
        {
            if (_engine == null) return;
            var newPos = TimeSpan.FromSeconds(PositionSeconds).Add(TimeSpan.FromSeconds(seconds));
            var dur = TimeSpan.FromSeconds(DurationSeconds);
            if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
            if (newPos > dur) newPos = dur;
            _engine.Seek(newPos);
        }

        public void CycleTimeDisplayMode()
        {
            int current = (int)_currentTimeDisplayMode;
            int next = (current + 1) % 3;
            CurrentTimeDisplayMode = (TimeDisplayMode)next;
        }

        public void InitializeInactivityMetadata(string title, string description, string logoUrl, string year = "", string duration = "", string rating = "", string chapterInfo = "")
        {
            InactivityTitle = title;
            InactivityDescription = description;
            InactivityYear = year;
            InactivityDuration = duration;
            InactivityRating = rating;
            InactivityChapterInfo = chapterInfo;
            
            if (string.IsNullOrEmpty(logoUrl))
            {
                InactivityLogoUrl = null;
                HasInactivityLogo = false;
                return;
            }

            // Senior Refinement: Ensure ImageSource creation is ALWAYS on the UI thread
            _dispatcher.TryEnqueue(() => 
            {
                try
                {
                    if (Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri))
                    {
                        InactivityLogoUrl = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
                        HasInactivityLogo = true;
                    }
                    else
                    {
                        InactivityLogoUrl = null;
                        HasInactivityLogo = false;
                    }
                }
                catch
                {
                    InactivityLogoUrl = null;
                    HasInactivityLogo = false;
                }
            });
        }

        public void Dispose()
        {
            DetachEngine();
            _controlHideTimer?.Stop();
            _inactivityOverlayTimer?.Stop();
            _engine?.Dispose();
        }
    }
}
