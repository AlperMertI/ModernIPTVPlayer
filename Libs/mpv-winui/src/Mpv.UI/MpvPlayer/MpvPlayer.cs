#nullable enable
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mpv.Core;
using Mpv.Core.Enums.Client;
using Mpv.Core.Enums.Player;
using MpvWinUI.Common;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Mpv.Core.Args;
using Windows.Storage;
using Mpv.Core.Enums.Render;
using Silk.NET.Core.Native;


namespace MpvWinUI;

public sealed partial class MpvPlayer : Control
{
    private static long _nextInstanceId;
    private static long _liveInstances;
    private readonly long _instanceId;

    public static long LiveInstanceCount => Interlocked.Read(ref _liveInstances);

    ~MpvPlayer()
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [FINALIZER] MpvPlayer finalizing for instance {_instanceId}");
    }

    public MpvPlayer()
    {
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        Interlocked.Increment(ref _liveInstances);
        DefaultStyleKey = typeof(MpvPlayer);
        LogMemory("ctor");
    }

    private bool _mpvGpuIsDirty = false;
    private bool _isDisposed = false;
    private volatile bool _isCleaningUp = false;
    private Mpv.Core.Interop.MpvRenderContextNative.MpvRenderUpdateCallback? _updateCallback;

    public static readonly DependencyProperty RenderApiProperty =
        DependencyProperty.Register("RenderApi", typeof(string), typeof(MpvPlayer), new PropertyMetadata("dxgi"));

    public string RenderApi
    {
        get => (string)GetValue(RenderApiProperty);
        set => SetValue(RenderApiProperty, value);
    }

    public static readonly DependencyProperty HdrComputePeakProperty =
        DependencyProperty.Register("HdrComputePeak", typeof(bool), typeof(MpvPlayer), new PropertyMetadata(true));

    public bool HdrComputePeak
    {
        get => (bool)GetValue(HdrComputePeakProperty);
        set => SetValue(HdrComputePeakProperty, value);
    }

    public bool IsHdrEnabled => _renderControl?.IsHdrEnabled ?? false;
    private float _appliedPeakOverride = 0;
    public float AppliedPeak
    {
        get
        {
            if (_appliedPeakOverride > 0) return _appliedPeakOverride;
            if (!IsHdrEnabled || _renderControl == null) return 0;
            
            float h = _renderControl.PeakLuminance;
            float s = _renderControl.SdrWhiteLevel;
            float g = Math.Max(s / 80.0f, 0.1f);
            float peak = h / g;
            return (int)Math.Round(Math.Max(peak, 80.0f));
        }
        private set => _appliedPeakOverride = value;
    }
    public float PeakLuminance => _renderControl?.PeakLuminance ?? 80f;
    public float SdrWhiteLevel => _renderControl?.SdrWhiteLevel ?? 80f;
    public float OsMaxLuminance => _renderControl?.OsMaxLuminance ?? 80f;
    public bool IsMediaLoaded => Player?.IsMediaLoaded() ?? false;

    public float ManualPeakLuminance
    {
        get => _renderControl?.ManualPeakLuminance ?? 0;
        set { if (_renderControl != null) _renderControl.ManualPeakLuminance = value; }
    }

    public string PreferredToneMapping { get; set; } = "auto";
    
    public IntPtr SharedTextureHandle => IntPtr.Zero;

    protected override void OnApplyTemplate()
    {
        _renderControl = (D3D11RenderControl)GetTemplateChild("RenderControl");
        
        if (_renderControl != null)
        {
            _renderControl.RenderFrame = Render;
            _renderControl.HdrStatusChanged += OnHdrStatusChanged;
            _renderControl.SwapChainPresented = () => 
            {
                if (_mpvGpuIsDirty)
                {
                    Player?.RenderContext?.ReportSwap();
                    _mpvGpuIsDirty = false;
                }
            };
            
            // Initial sync request (will run once player is ready)
            _ = SyncHdrStatusAsync();
            
            // Set render context update callback to signal our render loop
            _updateCallback = (ctx) => _renderControl.SignalUpdate();
        }
    }

    public async Task SyncHdrStatusAsync()
    {
        // [DEADLOCK_PREVENTION] Early exit if disposed or in the middle of cleanup
        if (_isDisposed || Player?.Client?.IsInitialized is not true || _renderControl == null)
            return;

        string renderApi = "dxgi";
        bool isHdrEnabled = false;
        bool computePeak = true;
        float sdrWhite = 80f;
        float peakLuma = 80f;

        // [THREAD-SAFETY] Read properties directly from _renderControl.
        // We avoid DispatcherQueue.TryEnqueue here because it's the primary source of 
        // deadlocks during application shutdown (UI thread awaiting StopLoopAsync 
        // while the background thread awaits the UI thread here).
        try
        {
            // These properties are simple getters for fields in D3D11RenderControl 
            // and are safe to read from any thread.
            isHdrEnabled = _renderControl.IsHdrEnabled;
            sdrWhite = _renderControl.SdrWhiteLevel;
            peakLuma = _renderControl.PeakLuminance;
            
            // For DependencyProperties, we use the cached values or safe defaults if on background thread
            renderApi = this.DispatcherQueue.HasThreadAccess ? RenderApi : "dxgi";
            computePeak = this.DispatcherQueue.HasThreadAccess ? HdrComputePeak : true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HDR_SYNC] Failed to read control properties: {ex.Message}");
            if (_isDisposed) return;
        }

        // 1. Content-based HDR Detection (Expanded for better accuracy)
        string colormatrix = await GetPropertyAsync("video-params/colormatrix");
        string colorprim = await GetPropertyAsync("video-params/primaries");
        bool isHdrContent = colormatrix == "bt.2020-ncl" || colormatrix == "dolbyvision" || colorprim == "bt.2020";
        
        // 2. Peak Calculation (Luminance Parity Logic)
        float gain = Math.Max(sdrWhite / 80.0f, 0.1f);
        float displayPeak = peakLuma / gain;
        int targetPeakNits = (int)Math.Round(Math.Max(displayPeak, 100.0f));

        async Task SetResilient(string prop, object val) {
            try {
                await SetPropertyAsync(prop, val);
            } catch { }
        }

        if (isHdrEnabled)
        {
            // [DISPLAY_HDR] The swapchain is in HDR mode. Manual hints required for legacy dxgi.
            await SetResilient("target-colorspace-hint", "yes");
            
            if (renderApi == "dxgi")
            {
                await SetResilient("target-trc", "pq");
                await SetResilient("target-prim", "bt.2020");
            }

            if (isHdrContent)
            {
                // [HDR10 Path] Content is 10-bit HDR. Use 10-bit output.
                await SetResilient("d3d11-output-format", "rgb10_a2");
                await SetResilient("fbo-format", "rgb10_a2");
                await SetResilient("target-peak", targetPeakNits);
                await SetResilient("hdr-compute-peak", computePeak ? "yes" : "no");
                
                Debug.WriteLine($"[HDR_AUTH] ACTIVE | Gain: {gain:F2} | TargetPeak: {targetPeakNits} | Content: {colormatrix} | Renderer: {renderApi} | ComputePeak: {computePeak}");
            }
            else
            {
                // [SDR-on-HDR Path] Content is SDR but display is HDR. 
                // We can use rgba8 for SDR content on HDR display to save GPU bandwidth.
                await SetResilient("d3d11-output-format", "rgba8");
                await SetResilient("fbo-format", "rgba8");
                await SetResilient("target-peak", "auto");
                Debug.WriteLine($"[HDR_AUTH] SDR_ON_HDR | Hint: yes | Renderer: {renderApi}");
            }
        }
        else
        {
            // [DISPLAY_SDR] Pure SDR Path. Always use 8-bit for maximum performance.
            await SetResilient("d3d11-output-format", "rgba8");
            await SetResilient("fbo-format", "rgba8");
            await SetResilient("target-colorspace-hint", "no");
            await SetResilient("target-trc", "srgb");
            Debug.WriteLine($"[HDR_AUTH] OFF | Standard SDR Path (8-bit enabled)");
        }
    }

    private void OnHdrStatusChanged(object? sender, bool isEnabled)
    {
        _ = SyncHdrStatusAsync();
    }

    public async Task OpenAsync(StorageFile file)
    {
        Player ??= new Player();

        if (!Player.Client.IsInitialized)
        {
            EnsureTemplateApplied();
            if (_renderControl == null) return;
            
            Player.PlaybackPositionChanged += OnPositionChanged;
            Player.PlaybackStateChanged += OnStateChanged;
            Player.PropertyChanged += OnPropertyChanged;
            _renderControl.Initialize();
            await _renderControl.WaitForHdrStatusAsync();
            Player.Client.SetProperty("vo", "libmpv");
            Player.Client.RequestLogMessage(MpvLogLevel.V);
            Player.LogMessageReceived += OnLogMessageReceived;
            await Player.InitializeDXGIAsync(_renderControl.DeviceHandle, _renderControl.ContextHandle, _renderControl.AdapterName, RenderApi, colorspace: (int)_renderControl.SwapChainColorSpace);
            
            // Register callback AFTER RenderContext is created by InitializeDXGIAsync
            if (_updateCallback != null) Player.RenderContext?.SetUpdateCallback(_updateCallback, IntPtr.Zero);
            
            Debug.WriteLine($"[LOG] MPV Player Initialized Successfully with API: {RenderApi}");
        }
    }

    private void OnStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        // [OPTIMIZATION] Removed SyncHdrStatusAsync from here. 
        // Initial setup handles the start, and OnHdrStatusChanged handles display changes.
        // Reading video-params should be done via property observation if needed.
    }

    public event EventHandler<Mpv.Core.Structs.Client.MpvEventProperty>? PropertyChanged;

    private void OnPositionChanged(object? sender, PlaybackPositionChangedEventArgs e)
    {
    }

    public void ObserveProperty(string name, Mpv.Core.Enums.Client.MpvFormat format = Mpv.Core.Enums.Client.MpvFormat.String)
    {
        if (Player?.Client?.IsInitialized == true && !_isDisposed)
        {
            Player.Client.ObserveProperty(name, format);
        }
    }

    public void UnObserveProperties(ulong requestId = 0)
    {
        if (Player?.Client?.IsInitialized == true && !_isDisposed)
        {
            Player.Client.UnObserveProperties(requestId);
        }
    }

    private void OnPropertyChanged(object? sender, Mpv.Core.Structs.Client.MpvEventProperty e)
    {
        PropertyChanged?.Invoke(this, e);
    }

    public void Play()
        => Player?.Play();

    public void Pause()
        => Player?.Pause();

    private void OnLogMessageReceived(object? sender, LogMessageReceivedEventArgs e)
    {
        Debug.WriteLine($"[{e.Level}]\t{e.Prefix}: {e.Message}");
    }

    public async Task InitializePlayerAsync(bool skipScripts = false)
    {
        Player ??= new Player();

        if (!Player.Client.IsInitialized)
        {
            EnsureTemplateApplied();
            if (_renderControl == null) return;

            Player.PlaybackPositionChanged += OnPositionChanged;
            Player.PlaybackStateChanged += OnStateChanged;
            Player.PropertyChanged += OnPropertyChanged;
            _renderControl.Initialize();
            await _renderControl.WaitForHdrStatusAsync();
            Player.Client.RequestLogMessage(MpvLogLevel.V);
            Player.LogMessageReceived += OnLogMessageReceived;
            
            // Note: vo is set to libmpv in InitializeDXGIAsync after gpu-next pre-init choice
            await Player.InitializeDXGIAsync(_renderControl.DeviceHandle, _renderControl.ContextHandle, _renderControl.AdapterName, RenderApi, skipScripts, colorspace: (int)_renderControl.SwapChainColorSpace);
            
            // Register callback
            if (_updateCallback != null) Player.RenderContext?.SetUpdateCallback(_updateCallback, IntPtr.Zero);
            
            Debug.WriteLine($"[LOG] MPV Player Initialized Successfully with API: {RenderApi}");
        }
    }

    public async Task OpenAsync(string url)
    {
        if (Player == null) await InitializePlayerAsync();
        await Player!.Client.ExecuteAsync($"loadfile \"{url.Replace("\"", "\\\"")}\"");
    }

    public async Task SetPropertyAsync<T>(string name, T value)
    {
        if (Player == null || value == null || _isDisposed) return;
        
        // Ensure decimal values ALWAYS use '.' (dot) regardless of system language.
        string valStr = value is IFormattable formattable
            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString() ?? string.Empty;

        int maxRetries = 3;
        int delay = 200;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // We use mpv_set_property_string (via the string overload) for maximum compatibility.
                await Task.Run(() => 
                {
                    try {
                        Player.Client.SetProperty(name, valStr);
                    } catch (Exception ex) {
                        // Capture it inside the task to prevent unhandled UI exceptions.
                        Debug.WriteLine($"[MPV_SET_ERR] Error setting '{name}' to '{valStr}': {ex.Message}");
                    }
                });
                return; // Success
            }
            catch (Exception ex) when (ex.Message.Contains("unsupported format") || i < maxRetries - 1)
            {
                if (i == maxRetries - 1) return; // Silent give up on last try
                await Task.Delay(delay);
                delay *= 2; 
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MPV_CRITICAL_ERR] {name}: {ex.Message}");
                return; // Exit silent
            }
        }
    }

    public async Task<string> GetPropertyAsync(string name)
    {
        if (Player == null || _isDisposed || Player.Client?.IsInitialized is not true) return "N/A";
        try
        {
            return await Task.Run(() => Player.Client.GetPropertyToString(name));
        }
        catch (Exception ex)
        {
            // Silently ignore "unavailable" errors which are common during loading/buffering
            if (!ex.Message.Contains("unavailable") && !ex.Message.Contains("not found"))
            {
                Debug.WriteLine($"[MPV_ERR] Failed to get property '{name}': {ex.Message}");
            }
            return "N/A";
        }
    }

    public async Task<bool> GetPropertyBoolAsync(string name)
    {
        if (Player == null || _isDisposed || Player.Client?.IsInitialized is not true) return false;
        try
        {
            return await Task.Run(() => Player.Client.GetPropertyToBoolean(name));
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("unavailable") && !ex.Message.Contains("not found"))
            {
                Debug.WriteLine($"[MPV_ERR] Failed to get bool property '{name}': {ex.Message}");
            }
            return false;
        }
    }

    public async Task<long> GetPropertyLongAsync(string name)
    {
        if (Player == null || _isDisposed || Player.Client?.IsInitialized is not true) return -1;
        try
        {
            return await Task.Run(() => Player.Client.GetPropertyToLong(name));
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("unavailable") && !ex.Message.Contains("not found"))
            {
                Debug.WriteLine($"[MPV_ERR] Failed to get long property '{name}': {ex.Message}");
            }
            return -1;
        }
    }

    public async Task ExecuteCommandAsync(params string[] args)
    {
        if (Player == null) return;
        try
        {
            await Player.Client.ExecuteAsync(args);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MPV_ERR] Failed to execute command '{string.Join(" ", args)}': {ex.Message}");
        }
    }

    public async Task<bool> TakeScreenshotAsync(string filePath)
    {
        if (Player == null) return false;
        try
        {
            // "screenshot-to-file" command: filename, flags
            // flags: "video" (no subtitles/OSD) or "subtitles" (with OSD) or "window"
            await Player.Client.ExecuteAsync(new string[] { "screenshot-to-file", filePath, "video" });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MPV_ERR] Screenshot failed: {ex.Message}");
            return false;
        }
    }


    public async Task CleanupAsync()
    {
        if (_isDisposed || _isCleaningUp) return;
        _isCleaningUp = true;
        
        var cleanupId = _instanceId;
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RACE_PROBE] CleanupAsync START for instance {cleanupId}");
        Interlocked.Increment(ref Mpv.Core.Interop.MpvRenderContextNative._globalCleanupRunning);

        _isDisposed = true;

        // 1. Unsubscribe from all events IMMEDIATELY to prevent "echo" tasks 
        // (like property changes triggering SyncHdrStatusAsync during shutdown)
        if (_renderControl != null)
        {
            _renderControl.HdrStatusChanged -= OnHdrStatusChanged;
        }

        if (Player != null)
        {
            Player.PlaybackPositionChanged -= OnPositionChanged;
            Player.PlaybackStateChanged -= OnStateChanged;
            Player.PropertyChanged -= OnPropertyChanged;
            Player.LogMessageReceived -= OnLogMessageReceived;

            // Stop live demuxing/cache fill before the render loop and D3D resources go away.
            // Exiting during buffering otherwise leaves noticeably more native memory behind.
            try { await Player.Client.ExecuteAsync("stop"); } catch { }
            try { await Player.Client.ExecuteAsync("playlist-clear"); } catch { }
            try { await Player.Client.ExecuteAsync("set cache no"); } catch { }
            try { await Task.Delay(75); } catch { }
            
            // 2. Stop the Render Loop
            if (_renderControl != null)
            {
                await _renderControl.StopLoopAsync();
            }

            // 3. Dispose of libmpv BEFORE disconnecting SwapChain
            // mpv needs the device to be alive to free its internal HW textures.
            if (_renderControl == null || !_renderControl.PreserveStateOnUnload)
            {
                try
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RACE_PROBE] Calling Player.DisposeAsync for instance {cleanupId}");
                    await Player.DisposeAsync();
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RACE_PROBE] Player.DisposeAsync COMPLETED for instance {cleanupId}");
                }
                catch (Exception ex) 
                { 
                    Debug.WriteLine($"[MPV_CLEANUP] Player.DisposeAsync ERROR: {ex.Message}");
                }
                Player = null;
            }
        }

        // 4. Cleanup native control resources
        if (_renderControl != null)
        {
            _renderControl.ClearRenderCallbacks();
            await _renderControl.DetachUiResourcesAsync();
            _renderControl.FlushContext();
            
            if (!_renderControl.PreserveStateOnUnload)
            {
                _renderControl.DestroyResources();
            }
            // [LEAK_FIX] Null out the render control reference to allow it to be GC'd independently of the player control
            _renderControl = null!;
        }

        Interlocked.Decrement(ref Mpv.Core.Interop.MpvRenderContextNative._globalCleanupRunning);
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RACE_PROBE] CleanupAsync FINISHED for instance {cleanupId}");
        Interlocked.Decrement(ref _liveInstances);
    }

    private void LogMemory(string stage, string? detail = null)
    {
    }

    public void SetDisplayFps(double fps)
    {
        try
        {
            Player?.Client.SetProperty("display-fps", fps);
        }
        catch { /* Not critical, handle gracefully */ }
    }

    public TimeSpan Duration
    {
        get
        {
            if (Player == null || !IsMediaLoaded) return TimeSpan.Zero;
            try
            {
                var val = Player.Client.GetPropertyToDouble("duration");
                return TimeSpan.FromSeconds(val);
            }
            catch 
            {
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (Player == null || !IsMediaLoaded) return TimeSpan.Zero;
            try
            {
                var val = Player.Client.GetPropertyToDouble("time-pos");
                return TimeSpan.FromSeconds(val);
            }
            catch 
            {
                return TimeSpan.Zero;
            }
        }
    }

    // Dönüş: True = Çizim yapıldı, False = Yapılmadı
    private unsafe bool Render(TimeSpan delta)
    {
        if (_isDisposed || _isCleaningUp)
        {
            if (_isCleaningUp) Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RACE_PROBE] Render REJECTED on instance {_instanceId} (Cleaning Up)");
            return false;
        }

        if (Player == null || Player.Client?.IsInitialized is not true || Player.RenderContext == null || _renderControl == null)
        {
            return false;
        }

        // Final guard for native handles
        if (Player.RenderContext.Handle.Handle == IntPtr.Zero)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RACE_PROBE] Render context handle is ZERO for instance {_instanceId}!");
            return false;
        }

        // 1. Check for update flags
        var updateSw = Stopwatch.StartNew();
        var flags = Player.RenderContext.Update();
        var updateMs = updateSw.Elapsed.TotalMilliseconds;
        
        // 2. We MUST render if mpv returns ANY non-zero flag (mostly Frame=1) 
        //    OR if a forced redraw is requested (resize/UI update).
        bool needsRender = (flags != 0) || _renderControl.ForceRedraw;
        
        if (!needsRender)
        {
            return false;
        }

        IntPtr backBufferHandle = _renderControl.RenderTargetHandle;
        
        if (backBufferHandle == IntPtr.Zero) return false;

        // 3. Çizimi gerçekleştir
        var renderSw = Stopwatch.StartNew();

        if (backBufferHandle == IntPtr.Zero ||
            _renderControl.CurrentWidth <= 1 || _renderControl.CurrentHeight <= 1 ||
            _renderControl.SwapChainWidth <= 1 || _renderControl.SwapChainHeight <= 1)
        {
            return false;
        }
        
        Player.RenderDXGI(backBufferHandle, 
                 _renderControl.SwapChainWidth, _renderControl.SwapChainHeight,
                 _renderControl.CurrentWidth, _renderControl.CurrentHeight,
                 block: false);
        
        var renderDuration = renderSw.Elapsed.TotalMilliseconds;
        _mpvGpuIsDirty = true;

        if (_renderControl.ForceRedraw)
        {
            // Zorunlu çizim isteği yerine getirildi, bayrağı indir.
            _renderControl.ForceRedraw = false;
        }

        return true;
    }

    public void EnableHandoffMode()
    {
        if (_renderControl != null)
        {
            _renderControl.PreserveStateOnUnload = true;
        }
    }

    public void DisableHandoffMode()
    {
        if (_renderControl != null)
        {
            _renderControl.PreserveStateOnUnload = false;
        }
    }

    public void SuspendResize()
    {
        if (_renderControl != null)
        {
            _renderControl.IsResizeSuspended = true;
        }
    }

    public void ResumeResize()
    {
        if (_renderControl != null)
        {
            _renderControl.IsResizeSuspended = false;
        }
    }

    public void Redraw()
    {
        if (_renderControl != null)
        {
            _renderControl.ForceRedraw = true;

        }
    }

    // [FIX] Force swap chain linking before handoff detachment
    public void EnsureSwapChainLinked()
    {
        _renderControl?.EnsureSwapChainLinked();
    }

    private void EnsureTemplateApplied()
    {
        if (_renderControl == null)
        {
            ApplyTemplate();
        }
        
        if (_renderControl == null)
        {
            Debug.WriteLine("[MpvPlayer] CRITICAL: RenderControl not found in template!");
        }
    }
}
