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
    
    // Render'da kullanılacak boyutlar - her zaman SwapChain ile sınırlı
    public int RenderWidth => _swapChainWidth > 0 ? _swapChainWidth : CurrentWidth;
    public int RenderHeight => _swapChainHeight > 0 ? _swapChainHeight : CurrentHeight;
    
    public bool ForceRedraw { get; set; }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static unsafe extern uint WaitForMultipleObjects(uint nCount, IntPtr* lpHandles, bool bWaitAll, uint dwMilliseconds);

    private const uint WAIT_OBJECT_0 = 0x00000000;

    public D3D11RenderControl()
    {
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }

    public unsafe void Initialize()
    {
        if (_disposed) return;

        if (_device.Handle == null)
        {
            LogSync("Stable Native Bridge Initializing...");
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
        Thread.CurrentThread.Priority = ThreadPriority.Highest; // Maksimum tepki hızı
        LogSync("Render Thread Engaged.");
        
        var cycleSw = new Stopwatch();
        var stageSw = new Stopwatch();
        IntPtr[] waitHandles = new IntPtr[2];
        bool lastPresentWasSuccess = false; // Takılmayı çözen kilit değişken

        while (!_cts.IsCancellationRequested && !_disposed)
        {
            cycleSw.Restart();
            uint waitResult = 0;

            stageSw.Restart();
            // AKILLI BEKLEME: Sadece eğer bir önceki turda Present yaptıysak VSync bekliyoruz.
            // Yapmadıysak beklemiyoruz (çünkü sinyal gelmez, 500ms donarız).
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
                _resizeEvent.Wait(1); 
            }
            var waitMs = (float)stageSw.Elapsed.TotalMilliseconds;

            var loopStamp = Stopwatch.GetTimestamp();
            stageSw.Restart();
            lock (_renderLock)
            {
                var lockMs = (float)stageSw.Elapsed.TotalMilliseconds;
                if (_disposed) break;

                float opMs = 0;
                if (_resizePending)
                {
                    ActiveResizeId = _pendingResizeId;
                    _resizePending = false;
                    _resizeEvent.Reset(); 
                    
                    var opSw = Stopwatch.StartNew();
                    PerformResize(force: false);
                    opMs = (float)opSw.Elapsed.TotalMilliseconds;
                    LogSync($"[RES_STEP_2] PerformResize DONE for ID: {ActiveResizeId} | Took: {opMs:F1}ms");
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

                    var totalMs = (float)cycleSw.Elapsed.TotalMilliseconds;
                    // UNMUTED: Performance logging for analysis
                    if (totalMs > 1.0f || opMs > 0) 
                    {
                         LogSync($"[DRAW_PERF] ID: {ActiveResizeId} | Total: {totalMs:F1}ms | Wait: {waitMs:F1}ms | Lock: {lockMs:F1}ms | Resize: {opMs:F1}ms | Render: {renderMs:F1}ms | Gpu: {presentMs:F1}ms | Drawn: {didDraw}");
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
            if (hr != 0) {
                 LogSync($"[(!!!) PRESENT_STATUS] HR: 0x{hr:X} | Handle: {(IntPtr)_swapChain.Handle:X}");
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

        // PERFORMANCE: Disable D3D11 internal locking (we use _renderLock for serialization)
        // using var multithread = _device.QueryInterface<ID3D11Multithread>();
        // if (multithread.Handle != null) multithread.Handle->SetMultithreadProtected(1);
        
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

        // READ DIMENSIONS ATOMICALLY: No long-blocking lock here
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

        // ÖNEMLİ DEĞİŞİKLİK: CurrentWidth/CurrentHeight HER ZAMAN güncellenmeli
        int oldWidth = CurrentWidth;
        int oldHeight = CurrentHeight;
        CurrentWidth = width;
        CurrentHeight = height;
        ForceRedraw = true;

        try
        {
            _frameLatencyWaitHandle = IntPtr.Zero; // Invalidate current handle

            int deltaW = Math.Abs(width - _swapChainWidth);
            int deltaH = Math.Abs(height - _swapChainHeight);
            bool needsPhysicalResize = _swapChain.Handle == null || deltaW > 16 || deltaH > 16 || force;
            string resizeMode = needsPhysicalResize ? "PHYSICAL" : "STRETCH";

            double resizeBuffersTime = 0;
            double getBufferTime = 0;

            if (needsPhysicalResize)
            {
                // 1. DRAIN GPU PIPELINE
                if (_context.Handle != null) {
                    _context.Handle->OMSetRenderTargets(0, null, null);
                }
                double flushTime = sw.Elapsed.TotalMilliseconds;

                // 2. ATOMIC BUFFER RELEASE
                IntPtr oldBuffer = Interlocked.Exchange(ref _atomicBackBuffer, IntPtr.Zero);
                if (oldBuffer != IntPtr.Zero)
                {
                    ((IUnknown*)oldBuffer)->Release();
                }
                double releaseTime = sw.Elapsed.TotalMilliseconds;

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
                resizeBuffersTime = sw.Elapsed.TotalMilliseconds - releaseTime;

                // 3. RECOVER NEW BUFFER
                if (_swapChain.Handle != null) {
                    ID3D11Texture2D* bufferRaw = null;
                    var hr = _swapChain.Handle->GetBuffer(0, SilkMarshal.GuidPtrOf<ID3D11Texture2D>(), (void**)&bufferRaw);
                    if (hr >= 0) {
                        Interlocked.Exchange(ref _atomicBackBuffer, (IntPtr)bufferRaw);
                        _swapChainWidth = width;
                        _swapChainHeight = height;
                    }
                }
                getBufferTime = sw.Elapsed.TotalMilliseconds - (releaseTime + resizeBuffersTime);
            }

            // 4. APPLY MATRIX TRANSFORM (Stretches buffer to window and handles High-DPI)
            if (_swapChain.Handle != null)
            {
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
            LogSync($"[RES_DEEP] ID: {ActiveResizeId} | Mode: {resizeMode} | Total: {totalTime:F2}ms | Lock: {lockTime:F2}ms | ResizeBuffers: {resizeBuffersTime:F2}ms | GetBuffer: {getBufferTime:F2}ms | Target: {width}x{height} | Buffer: {_swapChainWidth}x{_swapChainHeight}");
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
        LogSync($"[UI_BINDING] Enqueueing SetSwapChain for ID: {resizeIdAtEnq} | Handle: {handle:X}");
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
                    LogSync($"[UI_WATCHDOG] ID: {resizeIdAtEnq} | SetSwapChain HR: {hr:X} | Lag: {lagMs:F1}ms | Exec: {execMs:F1}ms | Handle: {handle:X}");
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
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [D3D_CTRL] OnUnloaded TRIGGERED");
        _disposed = true;
        _cts?.Cancel();
        _resizeEvent.Set();
        
        // Note: Actual resource destruction should happen AFTER libmpv is gone
        // to avoid "Device Removed" or access violation during render callbacks.
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

        // DIAGNOSTIC: Her RequestResize çağrısını logla
        int physW = (int)Math.Max(1, Math.Ceiling(_targetWidth * _targetScaleX));
        int physH = (int)Math.Max(1, Math.Ceiling(_targetHeight * _targetScaleY));
        LogSync($"[RES_STEP_1] RequestResize ID: {id} (force:{force}) | Old: {oldW:F0}x{oldH:F0} → New: {_targetWidth:F0}x{_targetHeight:F0} | Physical: {physW}x{physH} | Current: {CurrentWidth}x{CurrentHeight}");

        // Timer YOK - Her zaman işlem yap
        _pendingResizeId = id;
        _resizePending = true;
        _resizeEvent.Set();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // DIAGNOSTIC: SizeChanged event detayları
        LogSync($"[DIAG_SIZE] SizeChanged Event | Previous: {e.PreviousSize.Width:F0}x{e.PreviousSize.Height:F0} → New: {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");
        RequestResize(false);
    }

    private void LogSync(string message) => Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [SYNC] [Thread:{Environment.CurrentManagedThreadId}] {message}");
}
