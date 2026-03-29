using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mpv.Core;
using Mpv.Core.Enums.Client;
using Mpv.Core.Enums.Player;
using MpvWinUI.Common;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Mpv.Core.Args;
using Windows.Storage;
using Mpv.Core.Enums.Render;
using Silk.NET.Core.Native;


namespace MpvWinUI;

public sealed partial class MpvPlayer : Control
{
    public MpvPlayer()
    {
        DefaultStyleKey = typeof(MpvPlayer);
    }

    public static readonly DependencyProperty RenderApiProperty =
        DependencyProperty.Register("RenderApi", typeof(string), typeof(MpvPlayer), new PropertyMetadata("dxgi"));

    public string RenderApi
    {
        get => (string)GetValue(RenderApiProperty);
        set => SetValue(RenderApiProperty, value);
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

    public string PreferredToneMapping { get; set; } = "auto";

    protected override void OnApplyTemplate()
    {
        _renderControl = (D3D11RenderControl)GetTemplateChild("RenderControl");
        
        if (_renderControl != null)
        {
            _renderControl.RenderFrame = Render;
            _renderControl.HdrStatusChanged += OnHdrStatusChanged;
            _renderControl.SwapChainPresented = () => Player?.RenderContext?.ReportSwap();
            
            // Initial sync request (will run once player is ready)
            _ = SyncHdrStatusAsync();
        }
    }

    public async Task SyncHdrStatusAsync()
    {
        if (Player?.Client?.IsInitialized is not true || _renderControl == null)
            return;

        // Content-based HDR Detection (Matches mpv profile-cond logic)
        string colormatrix = await GetPropertyAsync("video-params/colormatrix");
        bool isHdrContent = colormatrix == "bt.2020-ncl" || colormatrix == "dolbyvision";
        
        // Display-based HDR Status
        bool isHdrEnabled = _renderControl.IsHdrEnabled;

        async Task SetResilient(string prop, object val) {
            try {
                await SetPropertyAsync(prop, val);
            } catch (Exception ex) {
                Debug.WriteLine($"[HDR_RESILIENT_ERR] Property '{prop}' failed: {ex.Message}");
            }
        }

        if (isHdrEnabled && isHdrContent)
        {
            // Full HDR Passthrough: Let Windows and MPV sync via hint=yes
            AppliedPeak = _renderControl.PeakLuminance;
            
            await SetResilient("target-colorspace-hint", "yes");
            await SetResilient("target-peak", "auto");
            await SetResilient("tone-mapping", PreferredToneMapping);
            
            // Standardize primary and trc for high-bitrate output
            await SetResilient("target-trc", "pq");
            await SetResilient("target-prim", "bt.2020");
            
            Debug.WriteLine($"[HDR_ACTIVE] Content: {colormatrix} | Hint: yes | Peak: auto | Tone: {PreferredToneMapping}");
        }
        else if (isHdrEnabled && !isHdrContent)
        {
            // SDR Content on HDR Screen: Use hint=yes to maintain OS UI brightness parity
            AppliedPeak = 0;
            await SetResilient("target-colorspace-hint", "yes");
            await SetResilient("target-peak", "auto");
            await SetResilient("target-trc", "srgb");
            Debug.WriteLine($"[HDR_SDR_PASS] SDR Content on HDR Screen. Hint: yes.");
        }
        else
        {
            // pure SDR mode
            AppliedPeak = 0;
            await SetResilient("target-colorspace-hint", "no");
            await SetResilient("target-trc", "srgb");
            Debug.WriteLine($"[HDR_OFF] Pure SDR mode.");
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
            _renderControl.Initialize();
            Player.Client.SetProperty("vo", "libmpv");
            Player.Client.RequestLogMessage(MpvLogLevel.V);
            Player.LogMessageReceived += OnLogMessageReceived;
            await Player.InitializeDXGIAsync(_renderControl.DeviceHandle, _renderControl.ContextHandle, _renderControl.AdapterName, RenderApi);
            Debug.WriteLine($"[LOG] MPV Player Initialized Successfully with API: {RenderApi}");
        }
    }

    private void OnStateChanged(object sender, PlaybackStateChangedEventArgs e)
    {
        if (e.NewState == PlaybackState.Decoding || e.NewState == PlaybackState.Playing)
        {
            // When video starts, ensure HDR peak is correctly synced with the OS slider
            _ = SyncHdrStatusAsync();
        }
    }

    private void OnPositionChanged(object sender, PlaybackPositionChangedEventArgs e)
    {
    }

    public void Play()
        => Player?.Play();

    public void Pause()
        => Player?.Pause();

    private void OnLogMessageReceived(object sender, LogMessageReceivedEventArgs e)
    {
        Debug.WriteLine($"[{e.Level}]\t{e.Prefix}: {e.Message}");
    }

    public async Task InitializePlayerAsync()
    {
        Player ??= new Player();

        if (!Player.Client.IsInitialized)
        {
            EnsureTemplateApplied();
            if (_renderControl == null) return;

            Player.PlaybackPositionChanged += OnPositionChanged;
            Player.PlaybackStateChanged += OnStateChanged;
            _renderControl.Initialize();
            Player.Client.SetProperty("vo", "libmpv");
            Player.Client.RequestLogMessage(MpvLogLevel.V);
            Player.LogMessageReceived += OnLogMessageReceived;
            await Player.InitializeDXGIAsync(_renderControl.DeviceHandle, _renderControl.ContextHandle, _renderControl.AdapterName, RenderApi);
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
        if (Player == null || value == null) return;
        
        // Ensure decimal values ALWAYS use '.' (dot) regardless of system language.
        string valStr = value is IFormattable formattable
            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString();

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
        if (Player == null) return "N/A";
        try
        {
            return await Task.Run(() => Player.Client.GetPropertyToString(name));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MPV_ERR] Failed to get property '{name}': {ex.Message}");
            return "N/A";
        }
    }

    public async Task<bool> GetPropertyBoolAsync(string name)
    {
        if (Player == null) return false;
        try
        {
            return await Task.Run(() => Player.Client.GetPropertyToBoolean(name));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MPV_ERR] Failed to get bool property '{name}': {ex.Message}");
            return false;
        }
    }

    public async Task<long> GetPropertyLongAsync(string name)
    {
        if (Player == null) return -1;
        try
        {
            return await Task.Run(() => Player.Client.GetPropertyToLong(name));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MPV_ERR] Failed to get long property '{name}': {ex.Message}");
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
        // 1. Stop the Render Loop FIRST to prevent access violations during disposal
        if (_renderControl != null)
        {
            await _renderControl.StopLoopAsync();
        }

        // 2. Dispose of libmpv AFTER rendering is guaranteed to have stopped
        // Skip disposal if handoff is active to preserve player state
        if (Player != null && (_renderControl == null || !_renderControl.PreserveStateOnUnload))
        {
            try
            {
                await Player.DisposeAsync();
            }
            catch (Exception) { }
            Player = null;
        }

        // 3. Destroy D3D11 Resources LAST
        if (_renderControl != null && (_renderControl == null || !_renderControl.PreserveStateOnUnload))
        {
            _renderControl.DestroyResources();
        }
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
            if (Player == null) return TimeSpan.Zero;
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
            if (Player == null) return TimeSpan.Zero;
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
        if (Player == null || Player.Client?.IsInitialized is not true || Player.RenderContext == null)
        {
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

        // DIAGNOSTIC: MPV'ye gönderilen boyutları logla (sadece ForceRedraw veya ilk frame için)
        long currentResizeId = _renderControl.ActiveResizeId;
        if (_renderControl.ForceRedraw)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RES_STEP_3] RenderDXGI START | ID: {currentResizeId} | Target: {_renderControl.CurrentWidth}x{_renderControl.CurrentHeight} | UpdateContext: {updateMs:F2}ms | ForceRedraw: True");
        }

        // 3. Çizimi gerçekleştir - RenderWidth/RenderHeight SwapChain sınırlarını aşmaz
        var renderSw = Stopwatch.StartNew();
        Player.RenderDXGI(backBufferHandle, _renderControl.RenderWidth, _renderControl.RenderHeight, block: false);
        var renderDuration = renderSw.Elapsed.TotalMilliseconds;

        if (_renderControl.ForceRedraw)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [RES_STEP_4] RenderDXGI DONE | ID: {currentResizeId} | Took: {renderDuration:F2}ms");
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
