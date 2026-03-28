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
    public float PeakLuminance => _renderControl?.PeakLuminance ?? 1000f;
    public float SdrWhiteLevel => _renderControl?.SdrWhiteLevel ?? 200f;

    protected override void OnApplyTemplate()
    {
        _renderControl = (D3D11RenderControl)GetTemplateChild("RenderControl");
        
        if (_renderControl != null)
        {
            // Event yerine Delegate ataması yaptık, böylece return değerini (bool) alabiliriz.
            _renderControl.RenderFrame = Render;
            _renderControl.HdrStatusChanged += OnHdrStatusChanged;
        }
    }

    private void OnHdrStatusChanged(object? sender, bool isEnabled)
    {
        if (Player?.Client?.IsInitialized is true)
        {
            _ = Task.Run(async () =>
            {
                if (isEnabled)
                {
                    float rawPeak = _renderControl?.PeakLuminance ?? 1000f;
                    int peak = (int)Math.Round(rawPeak); // Round to nearest integer (e.g. 617) for stability
                    
                    await SetPropertyAsync("target-colorspace-hint", "yes");
                    await SetPropertyAsync("target-trc", "pq");
                    await SetPropertyAsync("target-prim", "bt.2020");
                    await SetPropertyAsync("target-peak", peak.ToString());
                    
                    Debug.WriteLine($"[HDR_SYNC] MPV Tone-Mapping Optimized for Hardware: {peak} nits.");
                }
                else
                {
                    await SetPropertyAsync("target-colorspace-hint", "no");
                    await SetPropertyAsync("target-trc", "srgb");
                }
                Debug.WriteLine($"[HDR_SYNC] MPV Properties Updated - HDR Enabled: {isEnabled}");
            });
        }
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
        var valStr = value.ToString();
        int maxRetries = 5;
        int delay = 250;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await Task.Run(() => Player.Client.SetProperty(name, valStr));
                return; // Success
            }
            catch (Exception ex) when (ex.Message.Contains("unsupported format") || i < 2)
            {
                // Most common during initialization (VO not ready). 
                // We retry a few times to "apply when ready".
                if (i == maxRetries - 1) 
                {
                    Debug.WriteLine($"[MPV_ERR] Permanent Failure setting '{name}' to '{valStr}': {ex.Message}");
                    throw;
                }
                Debug.WriteLine($"[MPV_RETRY] Property '{name}' not ready. Retrying in {delay}ms... (Attempt {i + 1}/{maxRetries})");
                await Task.Delay(delay);
                delay *= 2; // Exponential backoff
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MPV_FATAL] Unexpected error setting '{name}': {ex.Message}");
                break;
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

        // 1. Yeni kare var mı kontrolü
        var updateSw = Stopwatch.StartNew();
        var flags = Player.RenderContext.Update();
        var updateMs = updateSw.Elapsed.TotalMilliseconds;
        bool hasFrame = (flags & MpvRenderUpdateFlag.Frame) != 0;

        // 2. Yeni kare yoksa VE zorla çizim istenmiyorsa (Resize vb.) çık.
        if (!hasFrame && !_renderControl.ForceRedraw)
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
