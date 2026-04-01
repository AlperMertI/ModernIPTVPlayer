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

public class D3D11RenderControl : ContentControl
{
    private SwapChainPanel _swapChainPanel;
    private ComPtr<ID3D11Device1> _device;
    private ComPtr<ID3D11DeviceContext1> _context;
    private ComPtr<IDXGISwapChain2> _swapChain; 
    
    private D3D11 _d3d11;
    private DXGI _dxgi;
    
    // Threading & Synchronization
    private Task _renderTask;
    private CancellationTokenSource _cts;
    private readonly object _renderLock = new();
    private IntPtr _frameLatencyWaitHandle = IntPtr.Zero;
    private readonly ManualResetEventSlim _resizeEvent = new(false);
    private readonly TaskCompletionSource<bool> _hdrInitTcs = new();
    
    // Resize State
    private readonly object _sizeLock = new();
    private bool _resizePending = false;
    private bool _disposed = false;
    private double _targetWidth, _targetHeight;
    private double _targetScaleX = 1.0, _targetScaleY = 1.0;
    private long _lastSizeChangedTicks;
    private long _lastPhysicalResizeTicks = 0; // Throttle for animations
    private bool _needsFirstFrameLink = false;

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
    private ColorSpaceType _swapChainColorSpace = ColorSpaceType.ColorSpaceRgbFullG22NoneP709;
    public ColorSpaceType SwapChainColorSpace => _swapChainColorSpace;
    private bool _isHdrEnabled = false;
    public bool IsHdrEnabled => _isHdrEnabled;

    private float _peakLuminance = 80.0f;
    private float _sdrWhiteLevel = 80.0f;
    private float _osMaxLuminance = 1000.0f;

    public float PeakLuminance => _peakLuminance;
    public float SdrWhiteLevel => _sdrWhiteLevel;
    public float OsMaxLuminance => _osMaxLuminance;
    
    // Render'da kullanılacak boyutlar - her zaman SwapChain ile sınırlı
    public int RenderWidth => _swapChainWidth > 0 ? _swapChainWidth : CurrentWidth;
    public int RenderHeight => _swapChainHeight > 0 ? _swapChainHeight : CurrentHeight;
    internal int SwapChainWidth => _swapChainWidth;
    internal int SwapChainHeight => _swapChainHeight;
    
    public bool ForceRedraw { get; set; }
    public bool PreserveStateOnUnload { get; set; } = false;
    
    // Shared Texture Support
    private SharedTextureHelper _sharedTexHelper;
    private bool _useSharedTexture = false;
    
    public bool UseSharedTexture 
    { 
        get => _useSharedTexture;
        set 
        {
            if (_useSharedTexture != value)
            {
                _useSharedTexture = value;
                if (value && DeviceHandle != IntPtr.Zero && CurrentWidth > 0 && CurrentHeight > 0)
                {
                    InitSharedTexture(CurrentWidth, CurrentHeight);
                }
                else if (!value)
                {
                    CleanupSharedTexture();
                }
            }
        }
    }
    
    public IntPtr SharedTextureHandle => _sharedTexHelper?.SharedHandle ?? IntPtr.Zero;

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

    public event EventHandler<bool> HdrStatusChanged;
    public event EventHandler Ready;
    
    private Microsoft.Graphics.Display.DisplayInformation _displayInfo;
    
    // DEĞİŞİKLİK: Void Action yerine, sonucun döndüğü Func kullanıyoruz
    public Func<TimeSpan, bool> RenderFrame; 
    public Action SwapChainPresented;

    public IntPtr DeviceHandle { get; private set; }
    public IntPtr ContextHandle { get; private set; }
    public string AdapterName { get; private set; } = "auto";
    public Task WaitForHdrStatusAsync() => _hdrInitTcs.Task;
    
    private IntPtr _atomicBackBuffer = IntPtr.Zero;
    public IntPtr RenderTargetHandle 
    { 
        get 
        {
            // Return shared texture if enabled and ready
            if (_useSharedTexture && _sharedTexHelper?.IsReady == true)
            {
                return _sharedTexHelper.SharedTexturePtr;
            }
            return _atomicBackBuffer;
        }
    }

    private Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan _lastFrameStamp;
    
    private IntPtr _cachedNativePanel = IntPtr.Zero;
    private IntPtr _lastLinkedHandle = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static unsafe extern uint WaitForMultipleObjects(uint nCount, IntPtr* lpHandles, bool bWaitAll, uint dwMilliseconds);

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
        SizeChanged += OnSizeChanged;
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
            
            // 2. STATUS & BASE PEAK are now handled by SyncHdrMetadata on the UI Thread.
            // This method (UpdateHdrStatus) runs on the background loop and only
            // processes the EDID physics and change detection.

            // 3. GET PHYSICAL TRUTH FROM EDID (Hardware Level)
            // Cache check: only re-scan registry if the monitor handle changed
            bool hardwarePeakFound = false;
            
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

            // 4. FINAL PEAK CALIBRATION
            if (hardwarePeakFound)
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
                _swapChainColorSpace = ColorSpaceType.ColorSpaceRgbFullG2084NoneP2020; 
                LogSync($"[HDR_STATUS] HDR ENABLED: Format={_swapChainFormat}, ColorSpace={_swapChainColorSpace} ({(int)_swapChainColorSpace})");
            }
            else
            {
                // Format: Default 8-bit (R8G8B8A8_UNORM)
                // ColorSpace: SDR (BT.709)
                _swapChainFormat = Format.FormatB8G8R8A8Unorm;
                _swapChainColorSpace = ColorSpaceType.ColorSpaceRgbFullG22NoneP709; // sRGB
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
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            
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
        UpdateRefreshRate(); // Re-check on init

        if (_device.Handle == null)
        {
            LogSync($"Stable Native Bridge Initializing... (TargetHz: {_monitorRefreshRate})");
            try
            {
                _d3d11 ??= D3D11.GetApi();
                _dxgi ??= DXGI.GetApi();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FATAL] Gateway API Load Failure: {ex}");
                throw;
            }

            CreateDevice();
            _swapChainPanel = new SwapChainPanel();
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Content = _swapChainPanel;

            _targetWidth = ActualWidth;
            _targetHeight = ActualHeight;
            _targetScaleX = _swapChainPanel.CompositionScaleX;
            _targetScaleY = _swapChainPanel.CompositionScaleY;

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

            UpdateSwapChain(); // Now create the swapchain with the correct format/colorspace

            Ready?.Invoke(this, EventArgs.Empty);
            StartRenderLoop();
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
        IntPtr[] waitHandles = new IntPtr[2];
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
                fixed (IntPtr* pHandles = waitHandles)
                {
                    waitResult = WaitForMultipleObjects(2, pHandles, false, 500); 
                }
            }
            else
            {
                _resizeEvent.Wait(4); // Optimized Idle Wait (1ms -> 4ms)
            }
            var waitMs = (float)stageSw.Elapsed.TotalMilliseconds;

            // 0. POLLING SDR SLIDER removed from hot path to avoid blocking the render thread.
            // HDR status and monitor peaks are now managed via UI events and initialized once.
            _frameCounter++;

            var loopStamp = Stopwatch.GetTimestamp();
            stageSw.Restart();
            lock (_renderLock)
            {
                var lockMs = (float)stageSw.Elapsed.TotalMilliseconds;
                if (_disposed) break;

                if (_resizePending)
                {
                    ActiveResizeId = _pendingResizeId;
                    _resizePending = false;
                    _resizeEvent.Reset(); 
                    
                    LogSync($"[RESIZE-2-START] ActiveResizeId:{ActiveResizeId} | CurrentWH:{CurrentWidth}x{CurrentHeight} | SwapChainWH:{_swapChainWidth}x{_swapChainHeight}");
                    var opSw = Stopwatch.StartNew();
                    PerformResize(force: false);
                    LogSync($"[RESIZE-2-END] Took:{opSw.Elapsed.TotalMilliseconds:F2}ms | After: CurrentWH:{CurrentWidth}x{CurrentHeight} | SwapChainWH:{_swapChainWidth}x{_swapChainHeight}");
                }

                try
                {
                    if (_atomicBackBuffer == IntPtr.Zero) {
                         continue;
                    }

                    // [FIX] Relaxed dimension check to avoid "stuck" render context on startup
                    if (CurrentWidth < 1 || CurrentHeight < 1) {
                         continue;
                    }

                    // Shared texture mutex acquisition
                    bool usingSharedTex = _useSharedTexture && _sharedTexHelper?.IsReady == true;
                    if (usingSharedTex)
                    {
                        if (!_sharedTexHelper.AcquireMutex(100))
                        {
                            // Skip frame if can't acquire mutex
                            continue;
                        }
                    }

                    stageSw.Restart();
                    var now = _stopwatch.Elapsed;
                    var delta = now - _lastFrameStamp;
                    _lastFrameStamp = now;
                    
                    bool didDraw = RenderFrame?.Invoke(delta) ?? false;
                    var renderMs = (float)stageSw.Elapsed.TotalMilliseconds;

                    // Release mutex after rendering
                    if (usingSharedTex)
                    {
                        _sharedTexHelper.ReleaseMutex();
                    }

                    float presentMs = 0;

                    if (didDraw || ForceRedraw)
                    {
                        stageSw.Restart();
                        
                        // Blit shared texture to backbuffer before presenting
                        if (usingSharedTex)
                        {
                            BlitSharedToBackBuffer();
                        }
                        
                        PresentFrame();
                        SwapChainPresented?.Invoke();
                        presentMs = (float)stageSw.Elapsed.TotalMilliseconds;
                        lastPresentWasSuccess = true;
                    }
                    else
                    {
                        lastPresentWasSuccess = false;
                    }

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
        }
    }

    private unsafe void PresentFrame()
    {
        if (_swapChain.Handle == null) return;
        try {
            int hr = _swapChain.Handle->Present(0, 0); 
            if (hr != 0 && hr != unchecked((int)0x887A0005)) { // Ignore DEVICE_REMOVED for log spam
                 // LogSync($"[(!!!) PRESENT_STATUS] HR: 0x{hr:X}");
            }
        } catch (Exception ex) {
            LogSync($"[FATAL] Present Exception: {ex.Message}");
        }
    }
    
    // ===== SHARED TEXTURE METHODS =====
    
    private void InitSharedTexture(int width, int height)
    {
        if (DeviceHandle == IntPtr.Zero) return;
        
        CleanupSharedTexture();
        
        _sharedTexHelper = new SharedTextureHelper(DeviceHandle, ContextHandle);
        bool isHdr = _swapChainFormat == Format.FormatR10G10B10A2Unorm;
        
        if (_sharedTexHelper.Create(width, height, isHdr))
        {
            LogSync($"[SHARED_TEX] Initialized: {width}x{height} | HDR: {isHdr}");
        }
        else
        {
            LogSync($"[SHARED_TEX] Failed to initialize");
            _sharedTexHelper.Dispose();
            _sharedTexHelper = null;
        }
    }
    
    private void CleanupSharedTexture()
    {
        if (_sharedTexHelper != null)
        {
            _sharedTexHelper.Dispose();
            _sharedTexHelper = null;
        }
    }
    
    private unsafe void BlitSharedToBackBuffer()
    {
        if (_sharedTexHelper == null || !_sharedTexHelper.IsReady || _atomicBackBuffer == IntPtr.Zero)
            return;
            
        try
        {
            _sharedTexHelper.CopyTo(_atomicBackBuffer);
        }
        catch (Exception ex)
        {
            LogSync($"[SHARED_TEX] Blit failed: {ex.Message}");
        }
    }
    
    private unsafe void CreateDevice()
    {
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
            AdapterName = SilkMarshal.PtrToString((IntPtr)desc.Description);
            LogSync($"Device Context Ready (VideoSupport=0x800). Adapter: {AdapterName}");
        }
        catch { AdapterName = "auto"; }
    }

    private void UpdateSwapChain()
    {
        lock (_renderLock) { PerformResize(force: true); }
    }

    private unsafe void PerformResize(bool force)
    {
        if (_disposed || _device.Handle == null) return;
        var sw = Stopwatch.StartNew();

        int width, height;
        double logW, logH, scaleX, scaleY;
        
        // HDR status is event-driven via AdvancedColorInfoChanged and _lastHMonitor cache.
        // No need to poll on every resize.

        lock (_sizeLock)
        {
            logW = _targetWidth;
            logH = _targetHeight;
            scaleX = _targetScaleX;
            scaleY = _targetScaleY;
            width = (int)Math.Max(1, Math.Ceiling(_targetWidth * _targetScaleX));
            height = (int)Math.Max(1, Math.Ceiling(_targetHeight * _targetScaleY));
        }
        
        LogSync($"[RESIZE-3-COMPUTE] target:{logW:F0}x{logH:F0} | scale:{scaleX:F2}x{scaleY:F2} | computed:{width}x{height} | oldCurrent:{CurrentWidth}x{CurrentHeight} | oldSwap:{_swapChainWidth}x{_swapChainHeight} | force:{force}");

        bool isSignificant = _swapChain.Handle == null || width != CurrentWidth || height != CurrentHeight;
        
        if (!isSignificant && !force) {
            LogSync($"[RESIZE-3-SKIP] Not significant and not forced");
            return;
        }

        double lockTime = sw.Elapsed.TotalMilliseconds;

        int oldWidth = CurrentWidth;
        int oldHeight = CurrentHeight;
        CurrentWidth = width;
        CurrentHeight = height;
        ForceRedraw = true;

        LogSync($"[RESIZE-4-STATE] CurrentWidth/Height set to {width}x{height} (was {oldWidth}x{oldHeight}) | ForceRedraw=true");

        try
        {
            _frameLatencyWaitHandle = IntPtr.Zero;

            int deltaW = Math.Abs(width - _swapChainWidth);
            int deltaH = Math.Abs(height - _swapChainHeight);
            
            // EXACT SIZING STRATEGY (Reverted from Elastic due to MPV constraints)
            // 1. Check if we actually need to touch the GPU
            bool needsRealloc = _swapChain.Handle == null || width != _swapChainWidth || height != _swapChainHeight || force;
            
            // 2. Throttle Logic (Only applies if we need a physical realloc)
            long nowTicks = Stopwatch.GetTimestamp();
            double msSinceLastPhysical = (nowTicks - _lastPhysicalResizeTicks) * 1000.0 / Stopwatch.Frequency;
            double throttleThreshold = (1000.0 / Math.Max(30, _monitorRefreshRate)); 
            bool throttleActive = msSinceLastPhysical < throttleThreshold;

            if (needsRealloc && throttleActive && !force && _swapChain.Handle != null)
            {
                needsRealloc = false; // Skip physical resize, just stretch for this frame
            }

            string resizeMode = needsRealloc ? "PHYSICAL" : "STRETCH";

            LogSync($"[RESIZE-5-DECISION] Mode:{resizeMode} | needsRealloc:{needsRealloc} | throttle:{throttleActive} | deltaW:{deltaW} | deltaH:{deltaH} | force:{force}");

            double resizeBuffersTime = 0;
            double getBufferTime = 0;
            double flushTime = 0;
            double releaseTime = 0;

            if (needsRealloc)
            {
                _lastPhysicalResizeTicks = nowTicks;
                LogSync($"[RESIZE-6-PHYSICAL-START] Reallocating to {width}x{height}");
                
                // 1. DRAIN
                if (_context.Handle != null) {
                    _context.Handle->OMSetRenderTargets(0, null, null);
                    _context.Handle->ClearState();
                }
                flushTime = sw.Elapsed.TotalMilliseconds - lockTime;

                // 2. RELEASE
                IntPtr oldBuffer = Interlocked.Exchange(ref _atomicBackBuffer, IntPtr.Zero);
                if (oldBuffer != IntPtr.Zero)
                {
                    ((IUnknown*)oldBuffer)->Release();
                }
                releaseTime = sw.Elapsed.TotalMilliseconds - (lockTime + flushTime);

                if (_swapChain.Handle == null)
                {
                    CreateSwapChain(width, height);
                }
                else
                {
                    // Try ResizeBuffers first (fast path ~0.1ms)
                    // ClearState() ensures all backbuffer references are released
                    var hr = _swapChain.Handle->ResizeBuffers(3, (uint)width, (uint)height, _swapChainFormat, (uint)SwapChainFlag.FrameLatencyWaitableObject);
                    if (hr < 0) {
                        // Fallback: recreate swapchain if ResizeBuffers fails
                        _swapChain.Dispose();
                        _swapChain = default;
                        CreateSwapChain(width, height);
                    }
                }
                
                // 3. COLOR SPACE
                UpdateColorSpace();
                
                resizeBuffersTime = sw.Elapsed.TotalMilliseconds - (lockTime + flushTime + releaseTime);

                // 3. RECOVER
                if (_swapChain.Handle != null) {
                    ID3D11Texture2D* bufferRaw = null;
                    var hr = _swapChain.Handle->GetBuffer(0, SilkMarshal.GuidPtrOf<ID3D11Texture2D>(), (void**)&bufferRaw);
                    if (hr >= 0) {
                        Interlocked.Exchange(ref _atomicBackBuffer, (IntPtr)bufferRaw);
                        _swapChainWidth = width;
                        _swapChainHeight = height;
                    }
                }
                getBufferTime = sw.Elapsed.TotalMilliseconds - (lockTime + flushTime + releaseTime + resizeBuffersTime);
                
                // 4. SHARED TEXTURE - Recreate if enabled
                if (_useSharedTexture && needsRealloc)
                {
                    LogSync($"[RESIZE-7-SHAREDTEX] InitSharedTexture({width}x{height}) | useSharedTex:{_useSharedTexture}");
                    InitSharedTexture(width, height);
                }
                
                LogSync($"[RESIZE-6-PHYSICAL-END] After realloc: swapChainWH:{_swapChainWidth}x{_swapChainHeight} | sharedTexReady:{_sharedTexHelper?.IsReady}");
            }

            if (_swapChain.Handle != null)
            {
                // ALWAYS match viewport to current logic size
                _swapChain.Handle->SetSourceSize((uint)width, (uint)height);

                var stretchX = (float)(logW / _swapChainWidth);
                var stretchY = (float)(logH / _swapChainHeight);
                var mat = new Silk.NET.Maths.Matrix3X2<float>(stretchX, 0, 0, stretchY, 0, 0);
                _swapChain.Handle->SetMatrixTransform((Silk.NET.DXGI.Matrix3X2F*)&mat);
                
                LogSync($"[RESIZE-8-TRANSFORM] SourceSize:{width}x{height} | stretch:{stretchX:F4}x{stretchY:F4} | logW/H:{logW:F0}x{logH:F0} | swapW/H:{_swapChainWidth}x{_swapChainHeight}");
            }

            UpdateWaitableObject();
            if (_swapChain.Handle != null && (IntPtr)_swapChain.Handle != _lastLinkedHandle) {
                 _needsFirstFrameLink = true;
            }
            
            double totalTime = sw.Elapsed.TotalMilliseconds;
            
            if (resizeMode == "PHYSICAL")
            {
                LogSync($"[RES_PERF] Mode: {resizeMode} | Total: {totalTime:F2}ms | Valid: {width}x{height} | ResBuf: {resizeBuffersTime:F2}ms");
            }
        


        }
        catch (Exception ex) { LogSync($"[FATAL] Resize Exception: {ex}"); }
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
        
        using var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>();
        if (sc3.Handle != null)
        {
            var hr = sc3.Handle->SetColorSpace1(_swapChainColorSpace);
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

            sc4.Handle->SetHDRMetaData(HdrMetadataType.HdrMetadataTypeHdr10, (uint)sizeof(HdrMetadataHdr10), &metadata);
            
            LogSync($"[DXGI_METADATA_SIGNAL] Result: 0x0 | Peak: {targetPeak:F0} nits | BT.2020 (Standard)");
        }
    }

    private unsafe void CreateSwapChain(int width, int height)
    {
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
            BufferCount = 3, BufferUsage = DXGI.UsageRenderTargetOutput,
            SampleDesc = new SampleDesc(1, 0), Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore,
            Flags = (uint)SwapChainFlag.FrameLatencyWaitableObject
        };

        IDXGISwapChain1* sc1Raw;
        var hr = dxgiFactory.Handle->CreateSwapChainForComposition((IUnknown*)_device.Handle, &desc, null, &sc1Raw);
        if (hr < 0) throw new Exception($"SC Creation Failure (0x{hr:X})");

        var sc1 = new ComPtr<IDXGISwapChain1>(sc1Raw);
        _swapChain = sc1.QueryInterface<IDXGISwapChain2>();
        sc1.Dispose();
    }
    


    private long _swapChainVersion = 0;

    private unsafe void UpdateSwapChainOnUI()
    {
        if (_disposed || _swapChainPanel == null || _swapChain.Handle == null) return;
        
        var handle = (IntPtr)_swapChain.Handle;
        long currentVersion = ++_swapChainVersion;
        
        bool needsLinking = (handle != _lastLinkedHandle);
        _lastLinkedHandle = handle;

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
                     var pBase = ((WinRT.IWinRTObject)_swapChainPanel).NativeObject.ThisPtr;
                     Guid g1w = NSwapChainPanelNative.IID_ISwapChainPanelNative;
                     if (Marshal.QueryInterface(pBase, ref g1w, out _cachedNativePanel) != 0) {
                         Guid g1u = NSwapChainPanelNative.IID_ISwapChainPanelNative_UWP;
                         Marshal.QueryInterface(pBase, ref g1u, out _cachedNativePanel);
                     }
                }

                if (_cachedNativePanel != IntPtr.Zero) {
                    IntPtr vtable = Marshal.ReadIntPtr(_cachedNativePanel);
                    IntPtr methodPtr = Marshal.ReadIntPtr(vtable, NSwapChainPanelNative.Slot_SetSwapChain * IntPtr.Size);
                    var setSc = (SetSwapChainDelegate)Marshal.GetDelegateForFunctionPointer(methodPtr, typeof(SetSwapChainDelegate));
                    
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
        else DispatcherQueue.TryEnqueue(Act);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapChainDelegate(IntPtr thisPtr, IntPtr swapChain);

    public async Task StopLoopAsync()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] StopLoopAsync STARTED");
        _cts.Cancel();
        _resizeEvent.Set(); // Wake up the loop if it's waiting

        if (_renderTask != null)
        {
            try
            {
                // Wait for the task to complete
                await _renderTask.ContinueWith(t => { }, TaskScheduler.Default);
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] RenderLoop TASK COMPLETED");
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
            
            // Cleanup shared texture first
            CleanupSharedTexture();
            
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
            _targetScaleX = _swapChainPanel.CompositionScaleX;
            _targetScaleY = _swapChainPanel.CompositionScaleY;
            scaleX = _targetScaleX;
            scaleY = _targetScaleY;
        }

        LogSync($"[RESIZE-1-REQ] ID:{id} | old:{(int)oldW}x{(int)oldH} | new:{(int)_targetWidth}x{(int)_targetHeight} | scale:{scaleX:F2}x{scaleY:F2} | force:{force} | suspended:{_isResizeSuspended}");

        _pendingResizeId = id;
        _resizePending = true;
        _resizeEvent.Set();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RequestResize(false);
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
            device.cb = (uint)Marshal.SizeOf(device);
            
            // Loop through monitor devices on this display
            uint i = 0;
            while (EnumDisplayDevices(deviceName, i++, ref device, 1)) 
            {
                string hardwareId = device.DeviceID;
                if (string.IsNullOrEmpty(hardwareId)) continue;
                
                LogSync($"[HDR_EDID_STEP] Found Monitor {i-1}: {hardwareId}");
                
                // Normalize hardware ID for registry path
                // Input: \\?\DISPLAY#SDC4178#4&32ada849&0&UID8388688#{e6f07b5f-...}
                // Output: DISPLAY\SDC4178\4&32ada849&0&UID8388688
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
                        byte[] edid = (byte[])key.GetValue("EDID");
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
            
            LogSync("[HDR_EDID_FAILED] No HDR signature found in EDID payload.");
        }
        catch (Exception ex)
        {
            LogSync($"[HDR_EDID_ERR] Universal Scan failed: {ex.Message}");
        }
        return 0;
    }

    private void LogSync(string message) => Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SYNC] [Thread:{Environment.CurrentManagedThreadId}] {message}");
}
