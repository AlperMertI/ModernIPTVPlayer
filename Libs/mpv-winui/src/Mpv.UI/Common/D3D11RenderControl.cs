#nullable enable
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Diagnostics;
using WinRT;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Microsoft.Win32;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace MpvWinUI.Common;

public partial class D3D11RenderControl : ContentControl
{
    private SwapChainPanel _swapChainPanel = default!;
    private ComPtr<ID3D11Device1> _device;
    private ComPtr<ID3D11DeviceContext1> _context;
    private ComPtr<IDXGISwapChain2> _swapChain; 
    
    private D3D11 _d3d11 = default!;
    private DXGI _dxgi = default!;
    
    // Threading & Synchronization
    private Task _renderTask = default!;
    private CancellationTokenSource _cts = default!;
    private readonly System.Threading.Lock _renderLock = new();
    private IntPtr _frameLatencyWaitHandle = IntPtr.Zero;
    private readonly ManualResetEventSlim _resizeEvent = new(false);
    private readonly ManualResetEventSlim _mpvUpdateEvent = new(false);
    private readonly TaskCompletionSource<bool> _hdrInitTcs = new();
    
    // Resize State
    private readonly System.Threading.Lock _sizeLock = new();
    private bool _resizePending = false;
    private bool _pendingResizeForce = false;
    private bool _disposed = false;
    private double _targetWidth, _targetHeight;
    private double _targetScaleX = 1.0, _targetScaleY = 1.0;
    private long _lastPhysicalResizeTicks = 0; // Throttle for animations
    private bool _needsFirstFrameLink = false;

    // [RESIZE OPTIMIZATION] Debounce-based resize start/end detection
    private bool _isResizing = false;
    private DispatcherQueueTimer? _resizeDebounceTimer;
    private const int RESIZE_DEBOUNCE_MS = 300; // Time after last SizeChanged to consider resize complete

    // Performance Tracking
    private long _resizeIdCounter = 0;
    private long _pendingResizeId = 0;
    public long ActiveResizeId { get; private set; }
    
    // Video Dimensions - MPV'nin render etmesi gereken boyutlar
    public int CurrentWidth { get; private set; }
    public int CurrentHeight { get; private set; }
    
    // SwapChain Dimensions - Gerçek texture boyutları
    private int _swapChainWidth;
    private int _swapChainHeight;
    private Format _swapChainFormat = Format.FormatB8G8R8A8Unorm;
    private Format _appliedSwapChainFormat = (Format)(-1); // State Tracking
    private MpvColorSpace _swapChainColorSpace = MpvColorSpace.RgbFullG22NoneP709;
    private MpvColorSpace _appliedSwapChainColorSpace = (MpvColorSpace)(-1); // State Tracking
    private float _appliedPeakLuminance = -1.0f; // State Tracking

    // Stable Canvas State
    // Stable Canvas State (Increased for 4K displays with 2.0x DPI)
    private int _maxMonitorWidth = 4096;
    private int _maxMonitorHeight = 2400;
    private bool _isMonitorInfoInitialized = false;
    public MpvColorSpace SwapChainColorSpace => _swapChainColorSpace;
    private bool _isHdrEnabled = false;
    public bool IsHdrEnabled => _isHdrEnabled;

    private float _peakLuminance = 80.0f;
    private float _sdrWhiteLevel = 80.0f;
    private float _osMaxLuminance = 1000.0f;

    public float PeakLuminance => _peakLuminance;
    public float SdrWhiteLevel => _sdrWhiteLevel;
    public float OsMaxLuminance => _osMaxLuminance;

    private float _manualPeakLuminance = 0;
    public float ManualPeakLuminance
    {
        get => _manualPeakLuminance;
        set
        {
            if (Math.Abs(_manualPeakLuminance - value) > 1.0f)
            {
                _manualPeakLuminance = value;
                UpdateHdrStatus();
            }
        }
    }
    
    // Render'da kullanılacak boyutlar - her zaman SwapChain ile sınırlı
    public int RenderWidth => _swapChainWidth > 0 ? _swapChainWidth : CurrentWidth;
    public int RenderHeight => _swapChainHeight > 0 ? _swapChainHeight : CurrentHeight;
    internal int SwapChainWidth => _swapChainWidth;
    internal int SwapChainHeight => _swapChainHeight;
    
    public bool ForceRedraw { get; set; }
    public bool PreserveStateOnUnload { get; set; } = false;
    
    public IntPtr SharedTextureHandle => IntPtr.Zero;

    // [PERF] Animation Optimization
    private bool _isResizeSuspended = false;
    public bool IsResizeSuspended
    {
        get => _isResizeSuspended;
        set
        {
            if (_isResizeSuspended != value)
            {
                _isResizeSuspended = value;
                if (!_isResizeSuspended)
                {
                    // Resume: Force update to catch up with final size
                    RequestResize(force: true);
                }
            }
        }
    }

    public event EventHandler<bool>? HdrStatusChanged;
    public event EventHandler? Ready;
    
    private Microsoft.Graphics.Display.DisplayInformation _displayInfo = default!;
    
    // DEĞİŞİKLİK: Void Action yerine, sonucun döndüğü Func kullanıyoruz
    public Func<TimeSpan, bool> RenderFrame = default!; 
    public Action SwapChainPresented = default!;
    
    public void SignalUpdate() => _mpvUpdateEvent.Set();

    public IntPtr DeviceHandle { get; private set; }
    public IntPtr ContextHandle { get; private set; }
    public string AdapterName { get; private set; } = "auto";
    public Task WaitForHdrStatusAsync() => _hdrInitTcs.Task;
    
    private IntPtr _atomicBackBuffer = IntPtr.Zero;
    public unsafe IntPtr RenderTargetHandle 
    { 
        get 
        {
            return _atomicBackBuffer;
        }
    }

    private Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan _lastFrameStamp;
    
    private IntPtr _cachedNativePanel = IntPtr.Zero;
    private IntPtr _lastLinkedHandle = IntPtr.Zero;

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetActiveWindow();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint Flags;
        [MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static readonly Guid IID_IDisplayInformation = Guid.Parse("bed11288-c17d-4dc9-bc77-1d167f0d6536");

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        public unsafe fixed char DeviceName[32];
        public unsafe fixed char DeviceString[128];
        public uint StateFlags;
        public unsafe fixed char DeviceID[128];
        public unsafe fixed char DeviceKey[128];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint WaitForMultipleObjects(uint nCount, IntPtr* lpHandles, [MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds);

    private const uint WAIT_OBJECT_0 = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVMODE
    {
        [MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
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
        [MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
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


    private int _monitorRefreshRate = 60;
    private IntPtr _lastHMonitor = IntPtr.Zero;
    private long _frameCounter = 0;

    public D3D11RenderControl()
    {
        try {
            var logPath = @"C:\Users\ASUS\Documents\ModernIPTVPlayer\control_debug.log";
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [CONTROL] Constructor Start\n");
        } catch { }
        
        this.Loaded += (s, e) => { };
        this.SizeChanged += OnSizeChanged;
        this.DefaultStyleKey = typeof(D3D11RenderControl);
        Unloaded += OnUnloaded;
        UpdateHdrStatus();
        UpdateRefreshRate();
    }

    private unsafe void UpdateHdrStatus()
    {
        float oldSdrWhite = _sdrWhiteLevel; // Capture before sync
        bool hdrWasEnabled = _isHdrEnabled;
        IntPtr hwnd = IntPtr.Zero;

        try
        {
            // 1. Identify window and monitor
            hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            
            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            bool hardwarePeakFound = false;
            
            // 2. MANUAL OVERRIDE (User Settings)
            if (_manualPeakLuminance > 0)
            {
                _peakLuminance = _manualPeakLuminance;
                LogSync($"[HDR_MANUAL] User Override Active! Peak: {_peakLuminance:F1} nits.");
            }
            else
            {
                // 3. STATUS & BASE PEAK are now handled by SyncHdrMetadata on the UI Thread.
            // This method (UpdateHdrStatus) runs on the background loop and only
            // processes the EDID physics and change detection.

            // 3. GET PHYSICAL TRUTH FROM EDID (Hardware Level)
            // Cache check: only re-scan registry if the monitor handle changed
            
            if (hMonitor == _lastHMonitor && _peakLuminance > 150)
            {
                hardwarePeakFound = true;
            }
            else
            {
                _lastHMonitor = hMonitor;
                try
                {
                    float edidPeak = GetPhysicalPeakFromEdid(hMonitor);
                    if (edidPeak > 300)
                    {
                        _peakLuminance = edidPeak;
                        hardwarePeakFound = true;
                        LogSync($"[HDR_EDID] MONITOR_CHANGE Detect! Peak: {_peakLuminance:F1} nits.");
                    }
                }
                catch (Exception edidEx)
                {
                    LogSync($"[HDR_EDID_ERR] Discovery failed: {edidEx.Message}");
                }
                }
            }

            // 4. FINAL PEAK CALIBRATION
            if (_manualPeakLuminance > 0)
            {
                // Already set by Manual Override
            }
            else if (hardwarePeakFound)
            {
                // Physical Truth prioritized
            }
            else if (_isHdrEnabled)
            {
                // Fallback to cached OS peak or generic 600
                _peakLuminance = (_osMaxLuminance > 250) ? _osMaxLuminance : 600.0f;
            }
            else
            {
                _peakLuminance = 80; // SDR
            }

            // 5. SIGNALING SELECTION (Switch to scRGB for Absolute HDR)
            if (_isHdrEnabled)
            {
                // Format: PQ 10-bit (R10G10B10A2_UNORM)
                // ColorSpace: HDR10 (G2084/BT.2020)
                // In this mode, luminance is absolute (PQ curve). 
                // Windows SDR White slider does NOT affect this swapchain.
                _swapChainFormat = Format.FormatR10G10B10A2Unorm; 
                _swapChainColorSpace = _isHdrEnabled ? MpvColorSpace.RgbFullG2084NoneP2020 : MpvColorSpace.RgbFullG22NoneP709;
                LogSync($"[HDR_STATUS] HDR ENABLED: Format={_swapChainFormat}, ColorSpace={_swapChainColorSpace} ({(int)_swapChainColorSpace})");
            }
            else
            {
                // Format: Default 8-bit (R8G8B8A8_UNORM)
                // ColorSpace: SDR (BT.709)
                _swapChainFormat = Format.FormatB8G8R8A8Unorm;
                _swapChainColorSpace = MpvColorSpace.RgbFullG22NoneP709; // sRGB
                LogSync($"[HDR_STATUS] SDR MODE: Format={_swapChainFormat}, ColorSpace={_swapChainColorSpace} ({(int)_swapChainColorSpace})");
            }

            // 6. APPLY CHANGES
            bool sdrLevelChanged = Math.Abs(oldSdrWhite - _sdrWhiteLevel) > 1.0f;
            
            if (hdrWasEnabled != _isHdrEnabled || sdrLevelChanged)
            {
                LogSync($"[LUM_CHANGE] HDR_Active: {_isHdrEnabled} | SdrWhite: {_sdrWhiteLevel:F1} nits | OsMaxLum: {_osMaxLuminance:F1} nits");
                HdrStatusChanged?.Invoke(this, _isHdrEnabled);
                if (_swapChain.Handle != null && hdrWasEnabled != _isHdrEnabled) RequestResize(force: true);
                else { UpdateColorSpace(); } // Ensure metadata stays in sync if only luminance changed
            }
        }
        catch (Exception ex)
        {
            LogSync($"[HDR_ERR] Critical sync failure: {ex.Message}");
        }
    }

    private void UpdateRefreshRate()
    {
        try
        {
            var ptr = GetForegroundWindow();
            if (ptr == IntPtr.Zero) return;

            var devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            
            if (EnumDisplaySettings(null, -1, ref devMode)) // ENUM_CURRENT_SETTINGS = -1
            {
                if (devMode.dmDisplayFrequency > 0)
                {
                    _monitorRefreshRate = devMode.dmDisplayFrequency;
                    // LogSync($"[REFRESH_RATE] Detected: {_monitorRefreshRate}Hz");
                }
            }
        }
        catch { /* Fallback to 60 */ }
    }

    public unsafe void Initialize()
    {
        if (_disposed) return;
        LogControl("Initialize() Called");
        UpdateRefreshRate(); // Re-check on init

        if (_device.Handle == null)
        {
            LogControl("Device is NULL, creating...");
            LogSync($"Stable Native Bridge Initializing... (TargetHz: {_monitorRefreshRate})");
            try
            {
                LogControl("Loading D3D11 API...");
#pragma warning disable CS0618 // Type or member is obsolete in 2.23.0 but required for context-free loading
                _d3d11 ??= Silk.NET.Direct3D11.D3D11.GetApi();
                LogControl("Loading DXGI API...");
                _dxgi ??= Silk.NET.DXGI.DXGI.GetApi();
#pragma warning restore CS0618
                LogControl("APIs Loaded Successfully");
            }
            catch (Exception ex)
            {
                LogControl($"FATAL API LOAD ERROR: {ex.Message}");
                Debug.WriteLine($"[FATAL] Gateway API Load Failure: {ex}");
                throw;
            }

            CreateDevice();
            _swapChainPanel = new SwapChainPanel();
            _swapChainPanel.CompositionScaleChanged += (s, e) => RequestResize(force: true);
            this.Loaded += (s, e) => 
            {
                if (this.XamlRoot != null)
                {
                    this.XamlRoot.Changed += (r, args) => RequestResize(force: true);
                }
            };
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Content = _swapChainPanel;

            _targetWidth = ActualWidth;
            _targetHeight = ActualHeight;
            // [CRITICAL FIX] WinUI 3 SwapChainPanel.CompositionScaleX always returns 1.0.
            _targetScaleX = this.XamlRoot?.RasterizationScale ?? 1.0;
            _targetScaleY = this.XamlRoot?.RasterizationScale ?? 1.0;

            // Initialize SwapChain AFTER we have a chance to sync HDR metadata 
            // (PerformResize will call UpdateHdrStatus but we'll call it again below)
            
            // UI-Thread HDR Synchronization
            try
            {
                IntPtr hwnd = GetActiveWindow();
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                if (windowId.Value != 0)
                {
                    _displayInfo = Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(windowId);
                    SyncHdrMetadata();
                    UpdateHdrStatus(); // Ensure colorspace is updated after metadata sync
                    _displayInfo.AdvancedColorInfoChanged += (s, e) => { SyncHdrMetadata(); UpdateHdrStatus(); };
                }
                else
                {
                    // Fallback signaling if window is not ready
                    _hdrInitTcs.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                LogSync($"[HDR_UI_ERR] UI Thread metadata tracking failed: {ex.Message}");
                _hdrInitTcs.TrySetResult(true);
            }

            // Initialize SwapChain ONLY if we have a valid size. 
            // If the control is 1x1 (initial state), we'll defer to OnSizeChanged.
            if (_targetWidth > 8 && _targetHeight > 8)
            {
                UpdateSwapChain(); 
                Ready?.Invoke(this, EventArgs.Empty);
                StartRenderLoop();
            }
            else
            {
                LogControl($"Deferred Initialization: Current Size {_targetWidth}x{_targetHeight} is too small.");
            }

            // [RESIZE OPTIMIZATION] Initialize debounce timer for resize start/end detection
            _resizeDebounceTimer = DispatcherQueue.CreateTimer();
            _resizeDebounceTimer.Interval = TimeSpan.FromMilliseconds(RESIZE_DEBOUNCE_MS);
            _resizeDebounceTimer.Tick += OnResizeDebounceTimerTick;
            _resizeDebounceTimer.Stop();
        }
    }

    private void SyncHdrMetadata()
    {
        if (_displayInfo == null) return;
        var colorInfo = _displayInfo.GetAdvancedColorInfo();
        if (colorInfo != null)
        {
            _isHdrEnabled = colorInfo.CurrentAdvancedColorKind == Microsoft.Graphics.Display.DisplayAdvancedColorKind.HighDynamicRange;
            _sdrWhiteLevel = (float)colorInfo.SdrWhiteLevelInNits;
            _osMaxLuminance = (float)colorInfo.MaxLuminanceInNits;
            LogSync($"[HDR_UI_SYNC] ColorKind: {colorInfo.CurrentAdvancedColorKind} | SdrWhite: {_sdrWhiteLevel:F1} | OsMaxLum: {_osMaxLuminance:F1}");
            _hdrInitTcs.TrySetResult(true);
        }
        else
        {
            _hdrInitTcs.TrySetResult(true);
        }
    }

    private void StartRenderLoop()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _renderTask = Task.Factory.StartNew(RenderLoop, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private unsafe void RenderLoop()
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; // Balanced Priority
        LogSync("Render Thread Engaged (AboveNormal).");
        
        var cycleSw = new Stopwatch();
        var stageSw = new Stopwatch();
        IntPtr[] waitHandles = new IntPtr[3];
        bool lastPresentWasSuccess = false; 

        while (!_cts.IsCancellationRequested && !_disposed)
        {
            cycleSw.Restart();
            uint waitResult = 0;

            stageSw.Restart();
            bool allowWait = lastPresentWasSuccess && _frameLatencyWaitHandle != IntPtr.Zero;

            if (allowWait)
            {
                waitHandles[0] = _frameLatencyWaitHandle;
                waitHandles[1] = _resizeEvent.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                waitHandles[2] = _mpvUpdateEvent.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                fixed (IntPtr* pHandles = waitHandles)
                {
                    waitResult = WaitForMultipleObjects(3, pHandles, false, 50); 
                }
            }
            else
            {
                // Fallback: wait for resize or mpv update (no frame latency wait)
                waitHandles[0] = _resizeEvent.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                waitHandles[1] = _mpvUpdateEvent.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                fixed (IntPtr* pHandles = waitHandles)
                {
                    waitResult = WaitForMultipleObjects(2, pHandles, false, 50);
                }
            }
            
            // Reset the update event as we are about to process potentially a frame
            _mpvUpdateEvent.Reset();
            var waitMs = (float)stageSw.Elapsed.TotalMilliseconds;

            // 0. POLLING SDR SLIDER removed from hot path to avoid blocking the render thread.
            // HDR status and monitor peaks are now managed via UI events and initialized once.
            _frameCounter++;

            var loopStamp = Stopwatch.GetTimestamp();
            stageSw.Restart();

            bool didDraw = false;
            bool needsPresent = false;
            long resizeStartTime = 0;
            long resizeEndTime = 0;
            long renderStartTime = 0;
            long renderEndTime = 0;
            long presentStartTime = 0;
            long presentEndTime = 0;
            long lockWaitStart = 0;
            long lockAcquired = 0;

            // [PERF] Track lock wait time
            lockWaitStart = Stopwatch.GetTimestamp();

            // [PERF] Narrow lock scope - only protect shared data access.
            // PresentFrame() is thread-safe and should run outside the lock
            // to avoid blocking resize events from the UI thread.
            lock (_renderLock)
            {
                lockAcquired = Stopwatch.GetTimestamp();
                double lockWaitMs = (lockAcquired - lockWaitStart) * 1000.0 / Stopwatch.Frequency;

                var lockMs = (float)stageSw.ElapsedMilliseconds;
                if (_disposed) break;

                if (_resizePending)
                {
                    ActiveResizeId = _pendingResizeId;
                    _resizePending = false;
                    bool force = _pendingResizeForce;
                    _pendingResizeForce = false;
                    _resizeEvent.Reset(); 
                    resizeStartTime = Stopwatch.GetTimestamp();
                    PerformResize(force: force);
                    resizeEndTime = Stopwatch.GetTimestamp();
                    double resizeMs = (resizeEndTime - resizeStartTime) * 1000.0 / Stopwatch.Frequency;
                    double lockContentionMs = (resizeStartTime - lockAcquired) * 1000.0 / Stopwatch.Frequency;
                    // Debug.WriteLine($"[PERF] RESIZE | lockWait:{lockWaitMs:F2}ms | lockContention:{lockContentionMs:F2}ms | resize:{resizeMs:F2}ms | total:{(resizeEndTime - lockWaitStart) * 1000.0 / Stopwatch.Frequency:F2}ms");
                }

                try
                {
                    if (_atomicBackBuffer == IntPtr.Zero) {
                         // [AUTOMATED RECOVERY] If backbuffer is missing, attempt to recreate swapchain
                         // Throttled to once every 2 seconds to avoid overhead.
                         if (_frameCounter % 120 == 0) 
                         {
                             Debug.WriteLine("[CTRL] Skip: No BackBuffer | ATTEMPTING RECOVERY...");
                             // Use Task.Run to avoid deadlocking the render thread on _renderLock (which UpdateSwapChain takes)
                             _ = Task.Run(() => { try { UpdateSwapChain(); } catch { } });
                         }
                         continue;
                    }

                    // [FIX] Relaxed dimension check to avoid "stuck" render context on startup
                    if (CurrentWidth <= 1 || CurrentHeight <= 1) {
                         if (_frameCounter % 120 == 0) Debug.WriteLine($"[CTRL] Skip: Invalid Size {CurrentWidth}x{CurrentHeight}");
                         continue;
                    }


                    stageSw.Restart();
                    var now = _stopwatch.Elapsed;
                    var delta = now - _lastFrameStamp;
                    _lastFrameStamp = now;
                    
                    renderStartTime = Stopwatch.GetTimestamp();
                    didDraw = RenderFrame?.Invoke(delta) ?? false;
                    renderEndTime = Stopwatch.GetTimestamp();
                    double renderMs = (renderEndTime - renderStartTime) * 1000.0 / Stopwatch.Frequency;

                    // [LEAK_DEBUG] Frame counter every 600 frames (~10s at 60fps)
                    if (didDraw && _frameCounter % 600 == 0)
                    {
                        Debug.WriteLine($"[LEAK_DEBUG] Frame #{_frameCounter} | SC: {_swapChainWidth}x{_swapChainHeight} | RenderMS: {renderMs:F2}");
                    }

                    needsPresent = didDraw || ForceRedraw;

                    if (_needsFirstFrameLink)
                    {
                        UpdateSwapChainOnUI();
                        _needsFirstFrameLink = false;
                    }

                }
                catch (Exception ex)
                {
                    LogSync($"[FATAL] Loop Warning: {ex.Message}");
                }
            }
            // [PERF] Lock released - PresentFrame runs outside the lock
            // so resize events from UI thread are not blocked.

            // [PERF] Log render timing
            if (renderEndTime > renderStartTime)
            {
                double lockWaitMs = (lockAcquired - lockWaitStart) * 1000.0 / Stopwatch.Frequency;
                double renderMs = (renderEndTime - renderStartTime) * 1000.0 / Stopwatch.Frequency;
                double lockContentionMs = (renderStartTime - lockAcquired) * 1000.0 / Stopwatch.Frequency;
                // Debug.WriteLine($"[PERF] RENDER | lockWait:{lockWaitMs:F2}ms | lockContention:{lockContentionMs:F2}ms | mpvRender:{renderMs:F2}ms");
            }

            float presentMs = 0;
            if (needsPresent)
            {
                presentStartTime = Stopwatch.GetTimestamp();
                PresentFrame();
                SwapChainPresented?.Invoke();
                presentEndTime = Stopwatch.GetTimestamp();
                presentMs = (float)((presentEndTime - presentStartTime) * 1000.0 / Stopwatch.Frequency);
                lastPresentWasSuccess = true;
                
                // Reset ForceRedraw after present so it doesn't trigger every frame
                ForceRedraw = false;
                
                // [PERF] Log present timing
                double presentMsD = (presentEndTime - presentStartTime) * 1000.0 / Stopwatch.Frequency;
                // if (presentMsD > 0.5) // Only log if notable
                //    Debug.WriteLine($"[PERF] PRESENT | {presentMsD:F2}ms");
            }
            else
            {
                lastPresentWasSuccess = false;
            }
        }
    }

    private unsafe void PresentFrame()
    {
        if (_swapChain.Handle == null) return;
        try {
            _swapChain.Handle->SetSourceSize((uint)_swapChainWidth, (uint)_swapChainHeight);

            bool exactMatch = _swapChainWidth == (int)_targetWidth && _swapChainHeight == (int)_targetHeight;

            if (exactMatch)
            {
                // [PERF] 1:1 Pixel Mapping — swapchain matches panel exactly.
                // Set EXACT inverse scale of the RasterizationScale to cancel DWM upscaling.
                // If DPI is 200%, the inverse scale is 0.5. DWM cancels out the 0.5 with its own 2.0 upscale,
                // resulting in a flawless 1:1 direct pixel copy (0% 3D GPU usage for stretching).
                float invScaleX = _targetScaleX > 0 ? (float)(1.0 / _targetScaleX) : 1.0f;
                float invScaleY = _targetScaleY > 0 ? (float)(1.0 / _targetScaleY) : 1.0f;

                if (Math.Abs(invScaleX - 1.0f) < 0.001f && Math.Abs(invScaleY - 1.0f) < 0.001f)
                {
                    _swapChain.Handle->SetMatrixTransform(null);
                }
                else
                {
                    var mat = new Silk.NET.Maths.Matrix3X2<float>(invScaleX, 0, 0, invScaleY, 0, 0);
                    _swapChain.Handle->SetMatrixTransform((Silk.NET.DXGI.Matrix3X2F*)&mat);
                }
            }
            else
            {
                // [RESIZE MODE] Swapchain != panel — uniform scale + centering to preserve aspect ratio.
                // mpv renders video with correct AR into the swapchain buffer.
                // We fit the buffer within the panel at uniform scale, centered.
                float scaleX = _swapChainWidth > 0 ? (float)(_targetWidth / _swapChainWidth) : 1.0f;
                float scaleY = _swapChainHeight > 0 ? (float)(_targetHeight / _swapChainHeight) : 1.0f;
                float scale = Math.Min(scaleX, scaleY);

                float offsetX = (float)((_targetWidth - _swapChainWidth * scale) / 2.0);
                float offsetY = (float)((_targetHeight - _swapChainHeight * scale) / 2.0);

                var mat = new Silk.NET.Maths.Matrix3X2<float>(scale, 0, 0, scale, offsetX, offsetY);
                _swapChain.Handle->SetMatrixTransform((Silk.NET.DXGI.Matrix3X2F*)&mat);
            }

            int hr = _swapChain.Handle->Present(0, 0); 
            if (hr != 0 && hr != unchecked((int)0x887A0005)) { // Ignore DEVICE_REMOVED for log spam
                 // LogSync($"[(!!!) PRESENT_STATUS] HR: 0x{hr:X}");
            }
        } catch (Exception ex) {
            LogSync($"[FATAL] Present Exception: {ex.Message}");
        }
    }
    
    
    private unsafe void CreateDevice()
    {
        LogControl("CreateDevice() Execution START");
        uint flags = (uint)CreateDeviceFlag.BgraSupport; 
        
        // Phase 13: Enable Video Support to allow d3d11va decoder to bind to this device
        // 0x800 = D3D11_CREATE_DEVICE_VIDEO_SUPPORT
        flags |= 0x800; 

        ID3D11Device* device;
        ID3D11DeviceContext* context;

        var hr = _d3d11.CreateDevice(null, D3DDriverType.Hardware, 0, flags, null, 0, D3D11.SdkVersion, &device, null, &context);
        if (hr < 0) throw new Exception($"Core Device Failure (HR: {hr})");

        _device = new ComPtr<ID3D11Device>(device).QueryInterface<ID3D11Device1>();
        _context = new ComPtr<ID3D11DeviceContext>(context).QueryInterface<ID3D11DeviceContext1>();
        
        DeviceHandle = (IntPtr)_device.Handle;
        ContextHandle = (IntPtr)_context.Handle;

        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice1>();
            IDXGIAdapter* adapter;
            dxgiDevice.Handle->GetAdapter(&adapter);
            using var dxgiAdapter = new ComPtr<IDXGIAdapter>(adapter);
            AdapterDesc desc;
            dxgiAdapter.Handle->GetDesc(&desc);
            AdapterName = SilkMarshal.PtrToString((IntPtr)desc.Description) ?? "unknown";
            LogSync($"Device Context Ready (VideoSupport=0x800). Adapter: {AdapterName}");
        }
        catch { AdapterName = "auto"; }
    }

    private void UpdateSwapChain()
    {
        lock (_renderLock) { PerformResize(force: true); }
    }

    private static int _resizeCallCount = 0;
    private unsafe void PerformResize(bool force)
    {
        if (_disposed || _device.Handle == null) return;
        int callId = ++_resizeCallCount;
        Debug.WriteLine($"[LEAK_DEBUG] PerformResize #{callId} force={force} | Current={CurrentWidth}x{CurrentHeight} | SwapChain={_swapChainWidth}x{_swapChainHeight} | isResizing={_isResizing}");
        long t0 = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;

        int width, height;
        double logW, logH, scaleX, scaleY;
        
        long tLockStart = Stopwatch.GetTimestamp();
        lock (_sizeLock)
        {
            logW = _targetWidth;
            logH = _targetHeight;
            scaleX = _targetScaleX;
            scaleY = _targetScaleY;
            width = (int)Math.Max(1, Math.Ceiling(_targetWidth * _targetScaleX));
            height = (int)Math.Max(1, Math.Ceiling(_targetHeight * _targetScaleY));
        }
        long t1 = Stopwatch.GetTimestamp();

        if (logW <= 0 || logH <= 0 || width <= 1 || height <= 1)
        {
            return;
        }

        bool isSignificant = _swapChain.Handle == null || width != CurrentWidth || height != CurrentHeight;
        
        if (!isSignificant && !force) {
            return;
        }

        int oldWidth = CurrentWidth;
        int oldHeight = CurrentHeight;
        CurrentWidth = width;
        CurrentHeight = height;
        // Force redraw after resize so:
        // 1. mpv re-renders with correct aspect ratio (play mode)
        // 2. PresentFrame is called to update the transform (pause mode)
        ForceRedraw = true;

        long t2 = Stopwatch.GetTimestamp();

        try
        {
            _frameLatencyWaitHandle = IntPtr.Zero;
            long t3 = Stopwatch.GetTimestamp();

            if (!_isMonitorInfoInitialized)
                UpdateMonitorInfo();

            // [RESIZE OPTIMIZATION] Determine target swapchain size based on resize state
            int stableWidth, stableHeight;
            bool needsRealloc;

            if (_isResizing)
            {
                // [RESIZE MODE - MEGA CANVAS OPTIMIZATION]
                // Eğer pencere dar iken büyütülürse ve SwapChain küçük donmuşsa, aspect-ratio hesaplaması 
                // dar kalmasına neden olur. Bu yüzden sürükleme başladığı an, arkaplanda monitör boyutunda dev 
                // bir tuval (Mega Canvas) açıyoruz. Yeniden boyutlandırma bitene kadar video bu dev tuval
                // içinde donanımsal MatrixTransform ile süzülerek istediği kadar büyüyebilir/küçülebilir.
                
                int targetMegaWidth = _maxMonitorWidth > 800 ? _maxMonitorWidth : 2560;
                int targetMegaHeight = _maxMonitorHeight > 600 ? _maxMonitorHeight : 1440;
                
                stableWidth = targetMegaWidth;
                stableHeight = targetMegaHeight;
                
                // Sadece tuval devasa boyuta henüz geçmemişse bir kere realloc yap (Stutter olmaz)
                needsRealloc = _swapChain.Handle == null || _swapChainWidth != stableWidth || _swapChainHeight != stableHeight || _appliedSwapChainFormat != _swapChainFormat || force;
                
                LogSync($"[PERFORM_RESIZE] RESIZE MODE (MEGA CANVAS) | Req: {width}x{height} | Canvas: {stableWidth}x{stableHeight} | Realloc: {needsRealloc}");
            }
            else
            {
                // [NORMAL MODE] Exact window dimensions for 1:1 pixel mapping
                stableWidth = Math.Max((int)width, 8);
                stableHeight = Math.Max((int)height, 8);
                
                // Only realloc if size actually changed
                needsRealloc = _swapChain.Handle == null ||
                              stableWidth != _swapChainWidth ||
                              stableHeight != _swapChainHeight ||
                              _appliedSwapChainFormat != _swapChainFormat ||
                              force;
                
                LogSync($"[PERFORM_RESIZE] NORMAL MODE | Req: {width}x{height} | SC: {_swapChainWidth}x{_swapChainHeight} | Realloc: {needsRealloc}");
            }

            long tDecision = Stopwatch.GetTimestamp();

            if (needsRealloc)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                _lastPhysicalResizeTicks = nowTicks;
                
                if (_context.Handle != null) {
                    _context.Handle->OMSetRenderTargets(0, null, null);
                }
                long tDrain = Stopwatch.GetTimestamp();

                IntPtr oldBuffer = Interlocked.Exchange(ref _atomicBackBuffer, IntPtr.Zero);
                if (oldBuffer != IntPtr.Zero)
                {
                    ((IUnknown*)oldBuffer)->Release();
                }
                long tRelease = Stopwatch.GetTimestamp();

                if (_swapChain.Handle == null)
                {
                    CreateSwapChain(stableWidth, stableHeight);
                }
                else
                {
                    _swapChainWidth = stableWidth;
                    _swapChainHeight = stableHeight;

                    Debug.WriteLine($"[LEAK_DEBUG] ResizeBuffers #{callId}: {_swapChainWidth}x{_swapChainHeight} -> {stableWidth}x{stableHeight} (format={_swapChainFormat})");
                    var hr = _swapChain.Handle->ResizeBuffers(2, (uint)stableWidth, (uint)stableHeight, _swapChainFormat, 0);
                    
                    if (hr < 0) {
                        _swapChain.Dispose();
                        _swapChain = default;
                        CreateSwapChain(stableWidth, stableHeight);
                    }
                    else
                    {
                        _swapChainWidth = stableWidth;
                        _swapChainHeight = stableHeight;
                        _appliedSwapChainFormat = _swapChainFormat;
                    }
                }
                long tResizeBuffers = Stopwatch.GetTimestamp();
                
                UpdateColorSpace();
                long tColorSpace = Stopwatch.GetTimestamp();

                if (_swapChain.Handle != null) {
                    ID3D11Texture2D* bufferRaw = null;
                    var bhr = _swapChain.Handle->GetBuffer(0, SilkMarshal.GuidPtrOf<ID3D11Texture2D>(), (void**)&bufferRaw);
                    
                    if (bhr >= 0 && bufferRaw != null) {
                        Interlocked.Exchange(ref _atomicBackBuffer, (IntPtr)bufferRaw);
                        ((IUnknown*)bufferRaw)->AddRef();
                    }
                }
                long tRecover = Stopwatch.GetTimestamp();

                UpdateWaitableObject();
                long tWaitObj = Stopwatch.GetTimestamp();

                // Debug.WriteLine($"[PERF-RAW] PHYSICAL | sizeLock:{(t1-tLockStart)*1000/freq:F3}ms | state:{(t2-t1)*1000/freq:F3}ms | zeroHandle:{(t3-t2)*1000/freq:F3}ms | decision:{(tDecision-t3)*1000/freq:F3}ms | drain:{(tDrain-tDecision)*1000/freq:F3}ms | release:{(tRelease-tDrain)*1000/freq:F3}ms | ResizeBuffers:{(tResizeBuffers-tRelease)*1000/freq:F3}ms | colorSpace:{(tColorSpace-tResizeBuffers)*1000/freq:F3}ms | recover:{(tRecover-tColorSpace)*1000/freq:F3}ms | waitObj:{(tWaitObj-tRecover)*1000/freq:F3}ms | TOTAL:{(tWaitObj-t0)*1000/freq:F3}ms");
            }
            else
            {
                long tEnd = Stopwatch.GetTimestamp();
                // Debug.WriteLine($"[PERF-RAW] STABLE | sizeLock:{(t1-tLockStart)*1000/freq:F3}ms | state:{(t2-t1)*1000/freq:F3}ms | zeroHandle:{(t3-t2)*1000/freq:F3}ms | decision:{(tDecision-t3)*1000/freq:F3}ms | TOTAL:{(tEnd-t0)*1000/freq:F3}ms");
            }
        
            if (_swapChain.Handle != null && (IntPtr)_swapChain.Handle != _lastLinkedHandle) {
                 _needsFirstFrameLink = true;
            }

        }
        catch (Exception ex) { LogSync($"[FATAL] Resize Exception: {ex}"); }
    }

    private void LogResizeTiming(bool force, int oldW, int oldH, int newW, int newH, 
        int bufW, int bufH,
        double tCompute, double tState, double tDecision,
        double tDrain, double tRelease, double tResizeBuffers, double tColorSpace, double tRecover,
        double tTotal)
    {
        string mode = (tResizeBuffers > 0) ? "PHYSICAL" : "STABLE_VIEWPORT";
        double tStateDelta = tState - tCompute;
        double tDecisionDelta = tDecision - tState;
        
        Debug.WriteLine($"[PERF] RESIZE {mode} | {oldW}x{oldH}→{newW}x{newH} | buf:{bufW}x{bufH}");
        Debug.WriteLine($"[PERF]   compute:{tCompute:F3}ms | state:{tStateDelta:F3}ms | decision:{tDecisionDelta:F3}ms");
        if (tResizeBuffers > 0)
        {
            double tDrainDelta = tDrain - tDecision;
            double tReleaseDelta = tRelease - tDrain;
            double tResizeDelta = tResizeBuffers - tRelease;
            double tColorDelta = tColorSpace - tResizeBuffers;
            double tRecoverDelta = tRecover - tColorSpace;
            double tRemainder = tTotal - tRecover;
            Debug.WriteLine($"[PERF]   drain:{tDrainDelta:F3}ms | release:{tReleaseDelta:F3}ms | ResizeBuffers:{tResizeDelta:F3}ms | colorSpace:{tColorDelta:F3}ms | recover:{tRecoverDelta:F3}ms | remainder:{tRemainder:F3}ms");
        }
        Debug.WriteLine($"[PERF]   TOTAL:{tTotal:F3}ms");
    }

    private unsafe void UpdateWaitableObject()
    {
        if (_swapChain.Handle != null)
        {
            _frameLatencyWaitHandle = (IntPtr)_swapChain.Handle->GetFrameLatencyWaitableObject();
            _swapChain.Handle->SetMaximumFrameLatency(1);
        }
    }

    private unsafe void UpdateColorSpace()
    {
        if (_swapChain.Handle == null) return;
        
        // [OPT] Only update DXGI state if actually changed
        if (_appliedSwapChainColorSpace == _swapChainColorSpace && !_isHdrEnabled)
        {
            return; 
        }

        using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
        if (sc3.Handle != null)
        {
            // Cast our clean wrapper enum to the native Silk.NET type at the interop boundary
            var hr = sc3.Handle->SetColorSpace1((Silk.NET.DXGI.ColorSpaceType)_swapChainColorSpace);
            _appliedSwapChainColorSpace = _swapChainColorSpace;
            LogSync($"[DXGI_COLOR_SYNC] Space: {_swapChainColorSpace} | Result: 0x{hr:X}");
        }

        if (_isHdrEnabled)
        {
            UpdateHdrMetadata();
        }
    }

    private unsafe void UpdateHdrMetadata()
    {
        if (_swapChain.Handle == null) return;

        // [OPT] Only update HDR metadata if physical peak actually changed
        if (Math.Abs(_appliedPeakLuminance - _peakLuminance) < 0.1f)
        {
             return;
        }

        using var sc4 = _swapChain.QueryInterface<IDXGISwapChain4>();
        if (sc4.Handle != null)
        {
            // Standard HDR10/PQ Metadata
            HdrMetadataHdr10 metadata = new HdrMetadataHdr10();
            
            // BT.2020 Primaries (Standard)
            metadata.RedPrimary[0] = 35400; metadata.RedPrimary[1] = 14600;
            metadata.GreenPrimary[0] = 8500; metadata.GreenPrimary[1] = 39850;
            metadata.BluePrimary[0] = 6550; metadata.BluePrimary[1] = 2300;
            metadata.WhitePoint[0] = 15635; metadata.WhitePoint[1] = 16450;

            float targetPeak = _peakLuminance; // Hardware Physical Peak
            if (targetPeak < 100) targetPeak = 400.0f; // Safety Floor
            
            metadata.MaxMasteringLuminance = (uint)(targetPeak * 10000); 
            metadata.MinMasteringLuminance = 10; // 0.001 nit
            metadata.MaxContentLightLevel = (ushort)targetPeak;
            metadata.MaxFrameAverageLightLevel = (ushort)(targetPeak * 0.9f);

            sc4.Handle->SetHDRMetaData(HdrMetadataType.Hdr10, (uint)sizeof(HdrMetadataHdr10), &metadata);
            _appliedPeakLuminance = _peakLuminance;
            
            LogSync($"[DXGI_METADATA_SIGNAL] Result: 0x0 | Peak: {targetPeak:F0} nits | BT.2020 (Standard)");
        }
    }

    private unsafe void CreateSwapChain(int width, int height)
    {
        LogControl($"Creating SwapChain {width}x{height}");
        if (_swapChain.Handle != null) return;
        
        if (_d3d11 == null) Initialize(); 

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice1>();
        IDXGIAdapter* adapter;
        dxgiDevice.Handle->GetAdapter(&adapter);
        using var dxgiAdapter = new ComPtr<IDXGIAdapter>(adapter);
        IDXGIFactory2* factory;
        dxgiAdapter.Handle->GetParent(SilkMarshal.GuidPtrOf<IDXGIFactory2>(), (void**)&factory);
        using var dxgiFactory = new ComPtr<IDXGIFactory2>(factory);

        SwapChainDesc1 desc = new SwapChainDesc1
        {
            Width = (uint)width, Height = (uint)height,
            Format = _swapChainFormat,
            BufferCount = 2, // 2 is safer for composition
            BufferUsage = DXGI.UsageRenderTargetOutput,
            SampleDesc = new SampleDesc(1, 0), 
            Scaling = Scaling.Stretch, // Use Stretch combined with Matrix Transform for clipping bypass
            SwapEffect = SwapEffect.FlipDiscard, // Enables MPO and minimum present latency
            AlphaMode = AlphaMode.Ignore,
            Flags = (uint)SwapChainFlag.FrameLatencyWaitableObject // Remove AllowTearing
        };

        _swapChainWidth = width;
        _swapChainHeight = height;
        _appliedSwapChainFormat = _swapChainFormat;

        IDXGISwapChain1* sc1Raw;
        var hr = dxgiFactory.Handle->CreateSwapChainForComposition((IUnknown*)_device.Handle, &desc, null, &sc1Raw);
        LogControl($"[DXGI_SC_CREATE] HR: 0x{hr:X} ({width}x{height})");
        if (hr < 0) throw new Exception($"SC Creation Failure (0x{hr:X})");

        var sc1 = new ComPtr<IDXGISwapChain1>(sc1Raw);
        _swapChain = sc1.QueryInterface<IDXGISwapChain2>();
        sc1.Dispose();
        LogControl($"[DXGI_SC_SUCCESS] Handle: {(IntPtr)_swapChain.Handle}");
    }
    


    private long _swapChainVersion = 0;

    private unsafe void UpdateSwapChainOnUI()
    {
        if (_disposed || _swapChainPanel == null || _swapChain.Handle == null) return;
        
        var handle = (IntPtr)_swapChain.Handle;
        long currentVersion = ++_swapChainVersion;
        
        bool needsLinking = (handle != _lastLinkedHandle);
        _lastLinkedHandle = handle;

        LogControl($"[UI_BIND_REQ] Handle: {handle:X} | NeedsLink: {needsLinking}");
        if (!needsLinking) return;

        long resizeIdAtEnq = ActiveResizeId;
        // LogSync($"[UI_BINDING] Enqueueing SetSwapChain for ID: {resizeIdAtEnq} | Handle: {handle:X}");
        Marshal.AddRef(handle);
        var queueTicks = Stopwatch.GetTimestamp();

        void Act()
        {
            var uiStartTicks = Stopwatch.GetTimestamp();
            try {
                if (_swapChainPanel == null || _disposed || currentVersion < _swapChainVersion) {
                    Marshal.Release(handle);
                    return;
                }

                if (_cachedNativePanel == IntPtr.Zero)
                {
                    // [AOT FIX] Use MarshalInspectable to safely get the ABI pointer for SwapChainPanel
                    IntPtr pBase = WinRT.MarshalInspectable<Microsoft.UI.Xaml.Controls.SwapChainPanel>.FromManaged(_swapChainPanel);
                     Guid g1w = NSwapChainPanelNative.IID_ISwapChainPanelNative;
                     if (Marshal.QueryInterface(pBase, in g1w, out _cachedNativePanel) != 0) {
                         Guid g1u = NSwapChainPanelNative.IID_ISwapChainPanelNative_UWP;
                         Marshal.QueryInterface(pBase, in g1u, out _cachedNativePanel);
                     }
                }

                if (_cachedNativePanel != IntPtr.Zero) {
                    IntPtr vtable = Marshal.ReadIntPtr(_cachedNativePanel);
                    IntPtr methodPtr = Marshal.ReadIntPtr(vtable, NSwapChainPanelNative.Slot_SetSwapChain * IntPtr.Size);
                    var setSc = Marshal.GetDelegateForFunctionPointer<SetSwapChainDelegate>(methodPtr);
                    
                    int hr = setSc(_cachedNativePanel, handle);
                    
                    var uiEndTicks = Stopwatch.GetTimestamp();
                    double lagMs = (uiStartTicks - queueTicks) * 1000.0 / Stopwatch.Frequency;
                    double execMs = (uiEndTicks - uiStartTicks) * 1000.0 / Stopwatch.Frequency;
                    // LogSync($"[UI_WATCHDOG] ID: {resizeIdAtEnq} | Lag: {lagMs:F1}ms | Exec: {execMs:F1}ms");
                }
            }
            catch (Exception ex) { LogSync($"[FATAL] UI Core Fault: {ex.Message}"); }
            finally { Marshal.Release(handle); }
        }

        if (DispatcherQueue.HasThreadAccess) Act();
        else DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, Act);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapChainDelegate(IntPtr thisPtr, IntPtr swapChain);

    public unsafe void DisconnectSwapChain()
    {
        if (_swapChainPanel == null) return;

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] DisconnectSwapChain STARTED");

        try
        {
            if (_cachedNativePanel != IntPtr.Zero)
            {
                var pBase = ((WinRT.IWinRTObject)_swapChainPanel).NativeObject.ThisPtr;
                Guid g1w = NSwapChainPanelNative.IID_ISwapChainPanelNative;
                IntPtr nativePanel = IntPtr.Zero;

                if (Marshal.QueryInterface(pBase, in g1w, out nativePanel) != 0)
                {
                    Guid g1u = NSwapChainPanelNative.IID_ISwapChainPanelNative_UWP;
                    Marshal.QueryInterface(pBase, in g1u, out nativePanel);
                }

                if (nativePanel != IntPtr.Zero)
                {
                    IntPtr vtable = Marshal.ReadIntPtr(nativePanel);
                    IntPtr methodPtr = Marshal.ReadIntPtr(vtable, NSwapChainPanelNative.Slot_SetSwapChain * IntPtr.Size);
                    var setSc = Marshal.GetDelegateForFunctionPointer<SetSwapChainDelegate>(methodPtr);

                    int hr = setSc(nativePanel, IntPtr.Zero);
                    Marshal.Release(nativePanel);

                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] DisconnectSwapChain: SetSwapChain(NULL) HR=0x{hr:X}");
                    _lastLinkedHandle = IntPtr.Zero;
                    _needsFirstFrameLink = true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D_CTRL] DisconnectSwapChain Error: {ex.Message}");
        }

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] DisconnectSwapChain COMPLETED");
    }

    public unsafe void FlushContext()
    {
        if (_context.Handle == null) return;

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] FlushContext STARTED");

        try
        {
            _context.Handle->ClearState();
            _context.Handle->Flush();

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] FlushContext COMPLETED");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D_CTRL] FlushContext Error: {ex.Message}");
        }
    }

    public async Task StopLoopAsync()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] StopLoopAsync STARTED");
        
        // 1. Cancel the token to request loop exit
        _cts.Cancel();
        
        // 2. Signal all events to wake up the RenderLoop from WaitForMultipleObjects
        _resizeEvent.Set();
        _mpvUpdateEvent.Set();

        if (_renderTask != null)
        {
            try
            {
                // Wait for the task to complete with a safety timeout to prevent deadlocks
                var timeoutTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(_renderTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] StopLoopAsync WARNING: Render loop did not exit in time. Forcing continuation.");
                }
                else
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] StopLoopAsync: Render loop exited gracefully.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[D3D_CTRL] StopLoopAsync Error: {ex.Message}");
            }
        }
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] StopLoopAsync FINISHED");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (PreserveStateOnUnload)
        {
             Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] OnUnloaded SKIPPED (PreserveStateOnUnload=true)");
             return;
        }

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] OnUnloaded TRIGGERED");
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _resizeEvent.Set(); } catch { }
    }

    public unsafe void DestroyResources()
    {
        lock (_renderLock) {
            _disposed = true;
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] DestroyResources STARTED");
            
            
            Interlocked.Exchange(ref _atomicBackBuffer, IntPtr.Zero);
            if (_swapChain.Handle != null) _swapChain.Dispose();
            if (_context.Handle != null) _context.Dispose();
            if (_device.Handle != null) _device.Dispose();
            if (_cachedNativePanel != IntPtr.Zero) { Marshal.Release(_cachedNativePanel); _cachedNativePanel = IntPtr.Zero; }
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] DestroyResources COMPLETED");
        }
        _resizeEvent.Dispose();
    }

    private void RequestResize(bool force = false)
    {
        if (_disposed || _swapChainPanel == null) return;
        
        // [PERF] Skip resize if suspended (during animation)
        if (_isResizeSuspended && !force) return;
        
        long id = Interlocked.Increment(ref _resizeIdCounter);
        double oldW, oldH;
        double scaleX, scaleY;
        lock (_sizeLock)
        {
            oldW = _targetWidth;
            oldH = _targetHeight;
            _targetWidth = ActualWidth;
            _targetHeight = ActualHeight;
            // [CRITICAL FIX] WinUI 3 SwapChainPanel.CompositionScaleX always returns 1.0.
            // We MUST use XamlRoot.RasterizationScale to get the physical DPI multiplier.
            _targetScaleX = this.XamlRoot?.RasterizationScale ?? 1.0;
            _targetScaleY = this.XamlRoot?.RasterizationScale ?? 1.0;
            scaleX = _targetScaleX;
            scaleY = _targetScaleY;
        }

        LogSync($"[RESIZE-1-REQ] ID:{id} | old:{(int)oldW}x{(int)oldH} | new:{(int)_targetWidth}x{(int)_targetHeight} | scale:{scaleX:F2}x{scaleY:F2} | force:{force} | suspended:{_isResizeSuspended}");

        _pendingResizeId = id;
        _pendingResizeForce = force;
        _resizePending = true;
        _resizeEvent.Set();
    }

    private unsafe void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        lock (_sizeLock)
        {
            _targetWidth = e.NewSize.Width;
            _targetHeight = e.NewSize.Height;
            _targetScaleX = this.XamlRoot?.RasterizationScale ?? 1.0;
            _targetScaleY = this.XamlRoot?.RasterizationScale ?? 1.0;
        }

        // [STABLE-CANVAS RE-INIT]
        // If we were deferred due to 1x1 size, start the loop now that we have a real size.
        if (_targetWidth > 8 && _targetHeight > 8)
        {
            if (_renderTask == null && _device.Handle != null)
            {
                LogControl($"Deferred Initialization COMPLETE: Starting loop at {_targetWidth}x{_targetHeight}");
                UpdateSwapChain();
                Ready?.Invoke(this, EventArgs.Empty);
                StartRenderLoop();
            }
            else
            {
                RequestResize();
            }
        }

        // [RESIZE OPTIMIZATION] Debounce-based resize start/end detection
        SetupResizeDebounceTimer();
    }

    private unsafe void SetupResizeDebounceTimer()
    {
        if (_disposed || _device.Handle == null) return;

        // First size change after idle = resize START
        if (!_isResizing)
        {
            _isResizing = true;
            LogSync($"[RESIZE-OPT] RESIZE STARTED — switching to monitor-sized swapchain");
            // Trigger immediate resize to monitor size
            RequestResize(force: true);
        }

        // Reset debounce timer
        _resizeDebounceTimer?.Stop();
        _resizeDebounceTimer?.Start();
    }

    private void OnResizeDebounceTimerTick(object sender, object e)
    {
        if (_disposed) return;

        _resizeDebounceTimer?.Stop();
        _isResizing = false;
        LogSync($"[RESIZE-OPT] RESIZE ENDED — switching to 1:1 pixel mapping");
        // Trigger resize to final window size
        RequestResize(force: true);
    }

    // [FIX] Force immediate swap chain linking — must be called from UI thread BEFORE detaching for handoff
    public unsafe void EnsureSwapChainLinked()
    {
        if (_disposed || _swapChainPanel == null || _swapChain.Handle == null) return;

        if (_needsFirstFrameLink || (IntPtr)_swapChain.Handle != _lastLinkedHandle)
        {
            UpdateSwapChainOnUI();
            _needsFirstFrameLink = false;
            LogSync("[FIX] EnsureSwapChainLinked: Forced immediate swap chain linkage.");
        }
    }

    private unsafe void UpdateMonitorInfo()
    {
        try
        {
            // Get the window handle for the current active window (closest to the app)
            IntPtr hwnd = GetActiveWindow();
            if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
            
            if (hwnd != IntPtr.Zero)
            {
                IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMonitor != IntPtr.Zero)
                {
                    MONITORINFOEX info = new MONITORINFOEX();
                    info.Size = Marshal.SizeOf(info);
                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        _maxMonitorWidth = Math.Abs(info.rcMonitor.Right - info.rcMonitor.Left);
                        _maxMonitorHeight = Math.Abs(info.rcMonitor.Bottom - info.rcMonitor.Top);
                        
                        // Fallback safety
                        if (_maxMonitorWidth < 800) _maxMonitorWidth = 1920;
                        if (_maxMonitorHeight < 600) _maxMonitorHeight = 1080;
                        
                        _isMonitorInfoInitialized = true;
                        LogSync($"[MONITOR_INFO] Detected Max Resolution: {_maxMonitorWidth}x{_maxMonitorHeight}");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSync($"[MONITOR_INFO_ERR] Failed to detect monitor: {ex.Message}");
        }

        // Default Fallbacks
        _maxMonitorWidth = 1920;
        _maxMonitorHeight = 1080;
        _isMonitorInfoInitialized = true;
    }

    private float GetPhysicalPeakFromEdid(IntPtr hMonitor)
    {
        try
        {
            // 1. Get Monitor Name (e.g. \\.\DISPLAY1)
            MONITORINFOEX info = new MONITORINFOEX();
            info.Size = Marshal.SizeOf(info);
            if (!GetMonitorInfo(hMonitor, ref info)) 
            {
                LogSync("[HDR_EDID_ERR] GetMonitorInfo failed.");
                return 0;
            }
            string deviceName = info.DeviceName;
            LogSync($"[HDR_EDID_STEP] Scanning Display: {deviceName}");

            // 2. Enum Display Devices to find Hardware ID
            DISPLAY_DEVICE device = new DISPLAY_DEVICE();
            unsafe { device.cb = (uint)sizeof(DISPLAY_DEVICE); }
            
            // Loop through monitor devices on this display
            uint i = 0;
            while (EnumDisplayDevices(deviceName, i++, ref device, 1)) 
            {
                string hardwareId;
                unsafe
                {
                    char* pId = device.DeviceID;
                    hardwareId = new string(pId);
                }

                if (string.IsNullOrEmpty(hardwareId)) continue;
                
                LogSync($"[HDR_EDID_STEP] Found Monitor {i-1}: {hardwareId}");
                
                // Normalize hardware ID for registry path
                string regId = hardwareId;
                if (regId.StartsWith(@"\\?\")) regId = regId.Substring(4);
                
                string[] parts = regId.Split('#');
                if (parts.Length >= 3)
                {
                    string cleanId = $@"DISPLAY\{parts[1]}\{parts[2]}";
                    string regPath = $@"SYSTEM\CurrentControlSet\Enum\{cleanId}\Device Parameters";
                    LogSync($"[HDR_EDID_STEP] Opening Registry: {regPath}");
                    
                    using (var key = Registry.LocalMachine.OpenSubKey(regPath))
                {
                    if (key != null)
                    {
                        byte[]? edid = (byte[]?)key.GetValue("EDID");
                        if (edid != null && edid.Length >= 128)
                        {
                            float peak = ParseHdrPeakFromEdid(edid);
                            LogSync($"[HDR_EDID_STEP] EDID binary found. Parsed Peak: {peak} nits.");
                            if (peak > 0) return peak;
                        }
                        else
                        {
                            LogSync("[HDR_EDID_STEP] EDID value missing in key.");
                        }
                    }
                    else
                    {
                        LogSync("[HDR_EDID_STEP] Registry key NOT found.");
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
            LogSync($"[HDR_EDID_ERR] Discovery failed: {ex.Message}");
        }
        return 0;
    }

    private float ParseHdrPeakFromEdid(byte[] edid)
    {
        try
        {
            if (edid == null || edid.Length < 128) return 0;
            
            // 1. Log Basic Info
            int extensions = edid[126];
            LogSync($"[HDR_EDID_SCAN] Extensions: {extensions} | Total Bytes: {edid.Length}");
            
            // 2. UNIVERSAL SIGNATURE SCAN (Deep Scan)
            // We search for the E6-06 signature (CTA-861 Static HDR Metadata Block) 
            // anywhere in the extension blocks. This bypasses structural quirks (like DisplayID wrapping).
            for (int i = 128; i < edid.Length - 6; i++)
            {
                // E6 06 is the magic signature for HDR Static Metadata
                if (edid[i] == 0xE6 && edid[i+1] == 0x06)
                {
                    LogSync($"[HDR_EDID_SCAN] Found HDR Signature (E6-06) at offset {i}");
                    
                    // Byte 4 from the start of the block header (i) is Max Luminance
                    // Based on CTA-861: 
                    // i+0: Tag (E6)
                    // i+1: Enum (06)
                    // i+2: Supported EOTFs
                    // i+3: Static Metadata Descriptors
                    // i+4: Max Luminance (Desired Content)
                    // i+5: Max Frame-Average Luminance
                    // i+6: Min Luminance
                    
                    byte maxLum = edid[i + 4];
                    byte maxAvg = edid[i + 5];
                    byte minLum = edid[i + 6];
                    
                    if (maxLum > 0)
                    {
                        float peakNits = 50.0f * (float)Math.Pow(2, maxLum / 32.0);
                        float avgNits = 50.0f * (float)Math.Pow(2, maxAvg / 32.0);
                        float blackNits = 0.005f * (float)Math.Pow(2, minLum / 32.0);
                        
                        LogSync($"[HDR_EDID_SCAN] >>> HARDWARE CALIBRATION FOUND <<<");
                        LogSync($"[HDR_EDID_SCAN] * Physical Peak: {peakNits:F1} nits (Raw: 0x{maxLum:X2})");
                        LogSync($"[HDR_EDID_SCAN] * Full Frame Max: {avgNits:F1} nits (Raw: 0x{maxAvg:X2})");
                        LogSync($"[HDR_EDID_SCAN] * Black Level: {blackNits:F4} nits (Raw: 0x{minLum:X2})");
                        
                        return peakNits;
                    }
                }
            }
            return 0;
        }
        catch { }
        return 0;
    }


    private void LogControl(string msg)
    {
        try
        {
            var logPath = @"C:\Users\ASUS\Documents\ModernIPTVPlayer\control_debug.log";
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [CONTROL] {msg}\n");
        }
        catch { }
        Debug.WriteLine($"[CONTROL] {msg}");
    }

    private void LogSync(string message) => Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SYNC] [Thread:{Environment.CurrentManagedThreadId}] {message}");
}
