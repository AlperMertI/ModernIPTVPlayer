using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MpvWinUI;
using Mpv.Core.Structs.Client;
using Mpv.Core.Enums.Client;

namespace ModernIPTVPlayer.Playback
{
    /// <summary>
    /// Implementation of <see cref="IPlaybackEngine"/> using the MPV player.
    /// </summary>
    public class MpvPlaybackEngine : IPlaybackEngine
    {
        private PlaybackState _state = PlaybackState.Idle;
        private TimeSpan _position = TimeSpan.Zero;
        private TimeSpan _duration = TimeSpan.Zero;
        
        // Internal Property Cache (Thread-Safe)
        private string _resolution = "-";
        private string _fps = "-";
        private string _videoCodec = "-";
        private string _audioCodec = "-";
        private string _bitrate = "-";
        private string _hardwareDecoding = "no";
        private string _renderer = "Direct3D 11";
        private string _cachedRenderApi = "d3d11";
        private string _bufferDuration = "-";
        private string _avSync = "-";
        private string _droppedFrames = "0";
        private string _colorspace = "-";
        private string _primaries = "-";
        private string _audioChannels = "-";
        private string _downloadSpeed = "-";
        private string _targetSdrWhite = "-";
        private string _appliedPeak = "-";
        private bool _hdrAvailable = false;
        
        private bool _isDisposed = false;
        private bool _isHdr;
        private string _sdrWhite = "-";
        private string _displayPeak = "-";

        private bool _isMuted;
        private double _volume = 100;
        private double _speed = 1.0;
        private bool _isMpvPaused;
        private bool _isMpvBuffering;
        private bool _isMpvSeeking;
        private bool _isMpvIdle;

        public PlaybackState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ENGINE] State: {_state} -> {value}");
                    _state = value;
                    _dispatcher.TryEnqueue(() => StateChanged?.Invoke(this, _state));
                }
            }
        }

        public TimeSpan Position
        {
            get => IsPlayerReady ? _player.Position : _position;
            private set
            {
                if (_position != value)
                {
                    _position = value;
                    _dispatcher.TryEnqueue(() => PositionChanged?.Invoke(this, _position));
                }
            }
        }

        public TimeSpan Duration
        {
            get => IsPlayerReady ? _player.Duration : _duration;
            private set
            {
                if (_duration != value)
                {
                    _duration = value;
                    _dispatcher.TryEnqueue(() => DurationChanged?.Invoke(this, _duration));
                }
            }
        }

        private bool IsPlayerReady => _player != null && _player.IsMediaLoaded;

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                _ = _player.SetPropertyAsync("volume", value.ToString(CultureInfo.InvariantCulture));
            }
        }

        public double Speed
        {
            get => _speed;
            set
            {
                _speed = value;
                _ = _player.SetPropertyAsync("speed", value.ToString(CultureInfo.InvariantCulture));
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                _ = _player.SetPropertyAsync("mute", value ? "yes" : "no");
            }
        }

        public bool IsSeekable { get; private set; }
        public string HardwareDecoding => _hardwareDecoding;
        public string BufferDuration => _bufferDuration;
        public string DroppedFrames => _droppedFrames;
        public string Resolution => _resolution;
        public string Fps => _fps;
        public string VideoCodec => _videoCodec;
        public string AudioCodec => _audioCodec;
        public bool IsHdr => _isHdr;
        public string SdrWhite => _sdrWhite;
        public string DisplayPeak => _displayPeak;
        public string Bitrate => _bitrate;
        public string Renderer => _renderer;
        public string AvSync => _avSync;
        public string Colorspace => _colorspace;
        public string Primaries => _primaries;
        public string AudioChannels => _audioChannels;
        public string DownloadSpeed => _downloadSpeed;
        public string TargetSdrWhite => _targetSdrWhite;
        public string AppliedPeak => _appliedPeak;
        public bool HdrAvailable => _hdrAvailable;

        private readonly MpvPlayer _player;
        private readonly Mpv.Core.Player _mpvCore;
        private readonly DispatcherQueue _dispatcher;

        public event EventHandler<PlaybackState>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler<TimeSpan>? DurationChanged;
        public event EventHandler<bool>? SeekingChanged;
        public event EventHandler<bool>? BufferingChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? MetadataChanged;

        public MpvPlaybackEngine(MpvPlayer player)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _mpvCore = player.Player;
            _dispatcher = player.DispatcherQueue;
            
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ENGINE] Initializing MpvPlaybackEngine");

            _player.PropertyChanged += OnPlayerPropertyChanged;
            
            // Cache the RenderApi immediately since it's a DependencyProperty 
            // and cannot be accessed from background threads later.
            _cachedRenderApi = _player.RenderApi;
            _mpvCore.PlaybackStateChanged += OnMpvCorePlaybackStateChanged;

            _player.ObserveProperty("seeking", MpvFormat.Flag);
            _player.ObserveProperty("paused-for-cache", MpvFormat.Flag);
            _player.ObserveProperty("core-idle", MpvFormat.Flag);
            _player.ObserveProperty("hwdec-current", MpvFormat.String);
            _player.ObserveProperty("video-bitrate", MpvFormat.Double);
            _player.ObserveProperty("demuxer-cache-duration", MpvFormat.Double);
            _player.ObserveProperty("avsync", MpvFormat.Double);
            _player.ObserveProperty("frame-drop-count", MpvFormat.Int64);
            _player.ObserveProperty("width", MpvFormat.Double);
            _player.ObserveProperty("height", MpvFormat.Double);
            _player.ObserveProperty("fps", MpvFormat.Double);
            _player.ObserveProperty("container-fps", MpvFormat.Double);
            _player.ObserveProperty("estimated-vf-fps", MpvFormat.Double);
            _player.ObserveProperty("video-codec", MpvFormat.String);
            _player.ObserveProperty("audio-codec", MpvFormat.String);
            _player.ObserveProperty("cache-speed", MpvFormat.Int64);
            _player.ObserveProperty("current-vo", MpvFormat.String);
            _player.ObserveProperty("current-gpu-context", MpvFormat.String);
            _player.ObserveProperty("video-out-params/max-luma", MpvFormat.Double);
            _player.ObserveProperty("video-out-params/sdr-white-nits", MpvFormat.Double);
            _player.ObserveProperty("video-out-params/device", MpvFormat.String);
            _player.ObserveProperty("video-params/colormatrix", MpvFormat.String);
            _player.ObserveProperty("video-params/colorlevels", MpvFormat.String);
            _player.ObserveProperty("video-params/primaries", MpvFormat.String);
            _player.ObserveProperty("video-params/gamma", MpvFormat.String);
            _player.ObserveProperty("audio-params/hr-channels", MpvFormat.String);
            
            // Initial call for renderer
            _renderer = _mpvCore.Client.GetPropertyToString("video-out-params/device") ?? "D3D11";
        }

        private void OnPlayerPropertyChanged(object? sender, MpvEventProperty e)
        {
            if (_isDisposed || e.DataPtr == IntPtr.Zero) return;
            
            try 
            {
                switch (e.Name)
                {
                    case "seeking":
                        _isMpvSeeking = Marshal.ReadInt32(e.DataPtr) != 0;
                        _dispatcher.TryEnqueue(() => SeekingChanged?.Invoke(this, _isMpvSeeking));
                        UpdateMpvState();
                        break;
                    case "paused-for-cache":
                        _isMpvBuffering = Marshal.ReadInt32(e.DataPtr) != 0;
                        _dispatcher.TryEnqueue(() => BufferingChanged?.Invoke(this, _isMpvBuffering));
                        UpdateMpvState();
                        break;
                    case "core-idle":
                        _isMpvIdle = Marshal.ReadInt32(e.DataPtr) != 0;
                        UpdateMpvState();
                        break;
                    case "idle-active":
                        _isMpvIdle = Marshal.ReadInt32(e.DataPtr) != 0;
                        UpdateMpvState();
                        break;
                    case "pause":
                        _isMpvPaused = Marshal.ReadInt32(e.DataPtr) != 0;
                        UpdateMpvState();
                        break;
                    case "time-pos":
                        if (e.Format == MpvFormat.Double)
                        {
                            double pos = Marshal.PtrToStructure<double>(e.DataPtr);
                            _position = TimeSpan.FromSeconds(pos);
                            _dispatcher.TryEnqueue(() => PositionChanged?.Invoke(this, _position));
                        }
                        break;
                    case "duration":
                        if (e.Format == MpvFormat.Double)
                        {
                            double dur = Marshal.PtrToStructure<double>(e.DataPtr);
                            _duration = TimeSpan.FromSeconds(dur);
                            _dispatcher.TryEnqueue(() => DurationChanged?.Invoke(this, _duration));
                        }
                        break;
                    case "video-codec":
                        _videoCodec = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(e.DataPtr)) ?? "-";
                        NotifyMetadataChanged();
                        break;
                    case "audio-codec":
                        _audioCodec = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(e.DataPtr)) ?? "-";
                        NotifyMetadataChanged();
                        break;
                    case "hwdec-current":
                        string hwd = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(e.DataPtr)) ?? "no";
                        _hardwareDecoding = hwd == "no" ? "SOFTWARE" : hwd.ToUpperInvariant();
                        NotifyMetadataChanged();
                        break;
                    case "video-bitrate":
                        if (e.Format == MpvFormat.Double)
                        {
                            double br = Marshal.PtrToStructure<double>(e.DataPtr);
                            _bitrate = br > 0 ? $"{(br / 1000000.0):F2} Mbps" : "-";
                            NotifyMetadataChanged();
                        }
                        break;
                    case "demuxer-cache-duration":
                        if (e.Format == MpvFormat.Double)
                        {
                            _bufferDuration = $"{Marshal.PtrToStructure<double>(e.DataPtr):F1}s";
                            NotifyMetadataChanged();
                        }
                        break;
                    case "avsync":
                        if (e.Format == MpvFormat.Double)
                        {
                            _avSync = $"{Marshal.PtrToStructure<double>(e.DataPtr) * 1000:F1}ms";
                            NotifyMetadataChanged();
                        }
                        break;
                    case "frame-drop-count":
                        _droppedFrames = Marshal.ReadInt64(e.DataPtr).ToString();
                        NotifyMetadataChanged();
                        break;
                    case "height":
                        if (e.Format == MpvFormat.Double)
                        {
                            double h = Marshal.PtrToStructure<double>(e.DataPtr);
                            string currentW = _resolution.Contains("x") ? _resolution.Split('x')[0] : "0";
                            _resolution = $"{currentW}x{(int)h}";
                            NotifyMetadataChanged();
                        }
                        break;
                    case "fps":
                    case "container-fps":
                    case "estimated-vf-fps":
                        if (e.Format == MpvFormat.Double)
                        {
                            double fVal = Marshal.PtrToStructure<double>(e.DataPtr);
                            if (fVal > 0) _fps = fVal.ToString("F2", CultureInfo.InvariantCulture);
                        }
                        NotifyMetadataChanged();
                        break;
                    case "video-params/primaries":
                    case "video-params/colormatrix":
                    case "video-params/gamma":
                    case "width":
                        if (e.Format == MpvFormat.Double && e.Name == "width")
                        {
                            double w = Marshal.PtrToStructure<double>(e.DataPtr);
                            string currentH = _resolution.Contains("x") ? _resolution.Split('x')[1] : "0";
                            _resolution = $"{(int)w}x{currentH}";
                            NotifyMetadataChanged();
                        }

                        if (_resolution == "-")
                        {
                            Debug.WriteLine($"[ENGINE_DEBUG] Triggering refresh on property change: {e.Name}");
                            RefreshTechnicalMetadata();
                        }
                        UpdateColorspace();
                        UpdatePrimaries();
                        break;
                    case "video-out-params/max-luma":
                    case "video-out-params/sdr-white-nits":
                    case "target-peak":
                        UpdateHdrMetadata();
                        break;
                    case "audio-bitrate":
                        NotifyMetadataChanged();
                        break;
                    case "cache-speed":
                        if (e.Format == MpvFormat.Int64)
                        {
                            long speed = Marshal.ReadInt64(e.DataPtr);
                            _downloadSpeed = FormatSpeed(speed);
                            NotifyMetadataChanged();
                        }
                        break;
                    case "current-vo":
                    case "current-gpu-context":
                        UpdateRendererLabel();
                        break;
                    default:
                        break;
                }
            }
            catch { }
        }

        private void OnMpvCorePlaybackStateChanged(object? sender, Mpv.Core.Args.PlaybackStateChangedEventArgs e)
        {
            // Decoding state in Mpv.Core corresponds to MPV's FILE_LOADED event.
            if (e.NewState == Mpv.Core.Enums.Player.PlaybackState.Decoding || 
                (e.NewState == Mpv.Core.Enums.Player.PlaybackState.Playing && _resolution == "-"))
            {
                RefreshTechnicalMetadata();
            }
        }

        private void UpdateMpvState()
        {
            if (_isMpvPaused) State = PlaybackState.Paused;
            else if (_isMpvIdle) State = PlaybackState.Idle;
            else if (_isMpvBuffering) State = PlaybackState.Buffering;
            else if (_isMpvSeeking) State = PlaybackState.Seeking;
            else State = PlaybackState.Playing;
        }

        private void RefreshResolution()
        {
            try
            {
                long w = _mpvCore.Client.GetPropertyToLong("video-out-params/res-w");
                long h = _mpvCore.Client.GetPropertyToLong("video-out-params/res-h");
                if (w > 0 && h > 0) _resolution = $"{w}x{h}";
            }
            catch { }
        }

        private void UpdateColorspace()
        {
            try
            {
                string matrix = _mpvCore.Client.GetPropertyToString("video-params/colormatrix") ?? "auto";
                string levels = _mpvCore.Client.GetPropertyToString("video-params/colorlevels") ?? "auto";
                string primaries = _mpvCore.Client.GetPropertyToString("video-params/primaries") ?? "auto";
                _colorspace = BuildMpvColorspaceSummary(matrix, levels, primaries);
                NotifyMetadataChanged();
            }
            catch { }
        }

        private void UpdatePrimaries()
        {
            try
            {
                string prim = _mpvCore.Client.GetPropertyToString("video-params/primaries") ?? "auto";
                string gamma = _mpvCore.Client.GetPropertyToString("video-params/gamma") ?? "auto";
                _primaries = BuildMpvHdrSummary(prim, gamma);
                
                _isHdr = gamma.Contains("pq", StringComparison.OrdinalIgnoreCase) || 
                         gamma.Contains("hlg", StringComparison.OrdinalIgnoreCase);
                         
                NotifyMetadataChanged();
            }
            catch { }
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

        private string FormatSpeed(long bytesPerSec)
        {
            if (bytesPerSec <= 0) return "0 KB/s";
            double mbps = (bytesPerSec * 8.0) / 1000000.0;
            if (bytesPerSec > 1024 * 1024) 
                return $"{(double)bytesPerSec / (1024 * 1024):F2} MB/s ({mbps:F1} Mbps)";
            return $"{(double)bytesPerSec / 1024:F0} KB/s ({mbps:F2} Mbps)";
        }

        private static string NormalizeMpvValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "N/A" || value == "no")
                return "-";
            return value.ToUpperInvariant();
        }

        private void UpdateHdrMetadata()
        {
            try
            {
                // Content Metadata (Luminance)
                try {
                    double luma = _mpvCore.Client.GetPropertyToDouble("video-out-params/max-luma");
                    if (luma > 0) _displayPeak = $"{luma:F0} nits";
                    else _displayPeak = "-";
                } catch { _displayPeak = "-"; }
                
                try {
                    double contentSdrWhite = _mpvCore.Client.GetPropertyToDouble("video-out-params/sdr-white-nits");
                    if (contentSdrWhite > 0) _sdrWhite = $"{contentSdrWhite:F0} nits";
                    else _sdrWhite = "-";
                } catch { _sdrWhite = "-"; }

                // Applied Values (From UI Control Logic)
                _appliedPeak = $"{_player.AppliedPeak:F0} nits";
                _targetSdrWhite = $"{_player.SdrWhiteLevel:F0} nits";
                _hdrAvailable = _player.IsHdrEnabled; 
                
                NotifyMetadataChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ENGINE_ERROR] UpdateHdrMetadata: {ex.Message}");
            }
        }

        public void RefreshTechnicalMetadata()
        {
            try
            {
                _audioCodec = _mpvCore.Client.GetPropertyToString("audio-codec") ?? "-";
                
                string hwdec = _mpvCore.Client.GetPropertyToString("hwdec-current") ?? "no";
                _hardwareDecoding = (string.IsNullOrEmpty(hwdec) || hwdec == "no") ? "SOFTWARE" : hwdec.ToUpperInvariant();
                UpdateRendererLabel();
                
                double w = _mpvCore.Client.GetPropertyToDouble("width");
                double h = _mpvCore.Client.GetPropertyToDouble("height");
                if (w > 0 && h > 0) _resolution = $"{(int)w}x{(int)h}";
                
                double fps = _mpvCore.Client.GetPropertyToDouble("fps");
                if (fps <= 0) fps = _mpvCore.Client.GetPropertyToDouble("container-fps");
                if (fps <= 0) fps = _mpvCore.Client.GetPropertyToDouble("estimated-vf-fps");
                if (fps > 0) _fps = fps.ToString("F2", CultureInfo.InvariantCulture);
                
                _audioChannels = _mpvCore.Client.GetPropertyToString("audio-params/hr-channels") ?? "-";
                
                // Initial fetch for dynamic props
                try {
                    long speed = _mpvCore.Client.GetPropertyToLong("cache-speed");
                    _downloadSpeed = FormatSpeed(speed);
                } catch { _downloadSpeed = "0 KB/s"; }

                try {
                    double bps = _mpvCore.Client.GetPropertyToDouble("video-bitrate");
                    _bitrate = $"{bps / 1000000:F1} Mbps";
                } catch { _bitrate = "0.0 Mbps"; }

                UpdateColorspace();
                UpdatePrimaries();
                UpdateHdrMetadata();
                
                Debug.WriteLine($"[ENGINE_DEBUG] Refresh: Res={_resolution}, VC={_videoCodec}, AC={_audioCodec}, HW={_hardwareDecoding}, Ren={_renderer}, FPS={_fps}, Speed={_downloadSpeed}");
                Debug.WriteLine($"[ENGINE_DEBUG] HDR: HDR={_isHdr}, Avail={_hdrAvailable}, Peak={_displayPeak}, TargetPeak={_appliedPeak}, SdrW={_sdrWhite}, TargetSdrW={_targetSdrWhite}");
                
                NotifyMetadataChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ENGINE_ERROR] RefreshTechnicalMetadata: {ex.Message}");
            }
        }

        private void UpdateRendererLabel()
        {
            try
            {
                string vo = _mpvCore.Client.GetPropertyToString("current-vo") ?? "";
                string context = _mpvCore.Client.GetPropertyToString("current-gpu-context") ?? "";
                
                // Base descriptive name from our cached control state
                string baseLabel = _cachedRenderApi == "d3d11" ? "GPU-NEXT (PLACEBO)" : "GPU (LEGACY DXGI)";
                
                if (string.IsNullOrEmpty(vo) && string.IsNullOrEmpty(context))
                {
                    _renderer = baseLabel;
                }
                else
                {
                    // Append the engine's detected context if available
                    string engineInfo = string.IsNullOrEmpty(context) ? vo.ToUpperInvariant() : context.ToUpperInvariant();
                    if (!string.IsNullOrEmpty(engineInfo) && !baseLabel.Contains(engineInfo))
                    {
                        _renderer = $"{baseLabel} / {engineInfo}";
                    }
                    else
                    {
                        _renderer = baseLabel;
                    }
                }
                
                Debug.WriteLine($"[RENDERER_DEBUG] Resulting _renderer: '{_renderer}'");
                NotifyMetadataChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RENDERER_DEBUG] Error in UpdateRendererLabel: {ex.Message}");
            }
        }

        private void NotifyMetadataChanged()
        {
            if (_isDisposed) return;
            _dispatcher.TryEnqueue(() => MetadataChanged?.Invoke(this, EventArgs.Empty));
        }

        public async Task LoadAsync(string url, double startPosition = 0)
        {
            await _player.OpenAsync(url);
            if (startPosition > 0)
            {
                await _player.ExecuteCommandAsync("seek", startPosition.ToString(CultureInfo.InvariantCulture), "absolute");
            }
        }

        public void Play() => _ = _player.SetPropertyAsync("pause", false);
        public void Pause() => _ = _player.SetPropertyAsync("pause", true);
        public void Stop() => _ = _player.ExecuteCommandAsync("stop");
        public void Seek(TimeSpan position) => _ = _player.ExecuteCommandAsync("seek", position.TotalSeconds.ToString(CultureInfo.InvariantCulture), "absolute");

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _player.PropertyChanged -= OnPlayerPropertyChanged;
            _mpvCore.PlaybackStateChanged -= OnMpvCorePlaybackStateChanged;
        }
    }
}
