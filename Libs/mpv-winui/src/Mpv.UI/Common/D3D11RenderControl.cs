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
    // SwapChain Dimensions - Gerçek texture boyutları
    private int _swapChainWidth;
    private int _swapChainHeight;
    
    // Render'da kullanılacak boyutlar - her zaman SwapChain ile sınırlı
    public int RenderWidth => _swapChainWidth > 0 ? _swapChainWidth : CurrentWidth;
    public int RenderHeight => _swapChainHeight > 0 ? _swapChainHeight : CurrentHeight;
    
    public bool ForceRedraw { get; set; }
    public bool PreserveStateOnUnload { get; set; } = false;

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

    public event EventHandler Ready;
    
    // DEĞİŞİKLİK: Void Action yerine, sonucun döndüğü Func kullanıyoruz
    public Func<TimeSpan, bool> RenderFrame; 

    public IntPtr DeviceHandle { get; private set; }
    public IntPtr ContextHandle { get; private set; }
    
    private IntPtr _atomicBackBuffer = IntPtr.Zero;
    public IntPtr RenderTargetHandle => _atomicBackBuffer;

    private Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan _lastFrameStamp;
    
    private IntPtr _cachedNativePanel = IntPtr.Zero;
    private IntPtr _lastLinkedHandle = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

    public D3D11RenderControl()
    {
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        UpdateRefreshRate();
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

            UpdateSwapChain();
            Ready?.Invoke(this, EventArgs.Empty);
            StartRenderLoop();
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
                    
                    var opSw = Stopwatch.StartNew();
                    PerformResize(force: false);
                    // opMs removed, logic logs inside PerformResize
                    // Detailed logging inside PerformResize now
                }

                try
                {
                    if (_atomicBackBuffer == IntPtr.Zero) {
                         continue;
                    }

                    stageSw.Restart();
                    var now = _stopwatch.Elapsed;
                    var delta = now - _lastFrameStamp;
                    _lastFrameStamp = now;
                    
                    bool didDraw = RenderFrame?.Invoke(delta) ?? false;
                    var renderMs = (float)stageSw.Elapsed.TotalMilliseconds;

                    float presentMs = 0;

                    if (didDraw || ForceRedraw)
                    {
                        stageSw.Restart();
                        PresentFrame();
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

    private unsafe void CreateDevice()
    {
        uint flags = (uint)CreateDeviceFlag.BgraSupport; 
        ID3D11Device* device;
        ID3D11DeviceContext* context;

        var hr = _d3d11.CreateDevice(null, D3DDriverType.Hardware, 0, flags, null, 0, D3D11.SdkVersion, &device, null, &context);
        if (hr < 0) throw new Exception($"Core Device Failure (HR: {hr})");

        _device = new ComPtr<ID3D11Device>(device).QueryInterface<ID3D11Device1>();
        _context = new ComPtr<ID3D11DeviceContext>(context).QueryInterface<ID3D11DeviceContext1>();
        
        DeviceHandle = (IntPtr)_device.Handle;
        ContextHandle = (IntPtr)_context.Handle;

        LogSync($"Device Context Ready. Dev: {DeviceHandle:X}");
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
        lock (_sizeLock)
        {
            logW = _targetWidth;
            logH = _targetHeight;
            scaleX = _targetScaleX;
            scaleY = _targetScaleY;
            width = (int)Math.Max(1, Math.Ceiling(_targetWidth * _targetScaleX));
            height = (int)Math.Max(1, Math.Ceiling(_targetHeight * _targetScaleY));
        }
        
        bool isSignificant = _swapChain.Handle == null || width != CurrentWidth || height != CurrentHeight;
        
        if (!isSignificant && !force) {
            return;
        }

        double lockTime = sw.Elapsed.TotalMilliseconds;

        int oldWidth = CurrentWidth;
        int oldHeight = CurrentHeight;
        CurrentWidth = width;
        CurrentHeight = height;
        ForceRedraw = true;

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

            double resizeBuffersTime = 0;
            double getBufferTime = 0;
            double flushTime = 0;
            double releaseTime = 0;

            if (needsRealloc)
            {
                _lastPhysicalResizeTicks = nowTicks;
                
                // 1. DRAIN
                if (_context.Handle != null) {
                    _context.Handle->OMSetRenderTargets(0, null, null);
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
                    var hr = _swapChain.Handle->ResizeBuffers(3, (uint)width, (uint)height, Format.FormatB8G8R8A8Unorm, (uint)SwapChainFlag.FrameLatencyWaitableObject);
                    if (hr < 0) {
                        _swapChain.Dispose();
                        CreateSwapChain(width, height);
                    }
                }
                
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
            }

            if (_swapChain.Handle != null)
            {
                // ALWAYS match viewport to current logic size
                _swapChain.Handle->SetSourceSize((uint)width, (uint)height);

                var stretchX = (float)(logW / _swapChainWidth);
                var stretchY = (float)(logH / _swapChainHeight);
                var mat = new Silk.NET.Maths.Matrix3X2<float>(stretchX, 0, 0, stretchY, 0, 0);
                _swapChain.Handle->SetMatrixTransform((Silk.NET.DXGI.Matrix3X2F*)&mat);
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
            Format = Format.FormatB8G8R8A8Unorm,
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
        _cts?.Cancel();
        _resizeEvent.Set();
    }

    public unsafe void DestroyResources()
    {
        lock (_renderLock) {
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
        lock (_sizeLock)
        {
            oldW = _targetWidth;
            oldH = _targetHeight;
            _targetWidth = ActualWidth;
            _targetHeight = ActualHeight;
            _targetScaleX = _swapChainPanel.CompositionScaleX;
            _targetScaleY = _swapChainPanel.CompositionScaleY;
        }

        // DIAGNOSTIC: Reduced frequency or level if needed
        // LogSync($"[RES_REQ] ID: {id}");

        _pendingResizeId = id;
        _resizePending = true;
        _resizeEvent.Set();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RequestResize(false);
    }

    private void LogSync(string message) => Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SYNC] [Thread:{Environment.CurrentManagedThreadId}] {message}");
}
