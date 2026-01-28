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

    protected override void OnApplyTemplate()
    {
        _renderControl = (D3D11RenderControl)GetTemplateChild("RenderControl");
        
        if (_renderControl != null)
        {
            // Event yerine Delegate ataması yaptık, böylece return değerini (bool) alabiliriz.
            _renderControl.RenderFrame = Render;
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
            Player.Client.RequestLogMessage(MpvLogLevel.Info);
            Player.LogMessageReceived += OnLogMessageReceived;
            await Player.InitializeDXGIAsync(_renderControl.DeviceHandle, _renderControl.ContextHandle);
            Debug.WriteLine("[LOG] MPV Player Initialized Successfully.");
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
            Player.Client.RequestLogMessage(MpvLogLevel.Info);
            Player.LogMessageReceived += OnLogMessageReceived;
            await Player.InitializeDXGIAsync(_renderControl.DeviceHandle, _renderControl.ContextHandle);
            Debug.WriteLine("[LOG] MPV Player Initialized Successfully.");
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
        try
        {
            await Task.Run(() => Player.Client.SetProperty(name, valStr));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MPV_ERR] Failed to set property '{name}' to '{valStr}': {ex.Message}");
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

    public async Task CleanupAsync()
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] CleanupAsync STARTED (Strict Sequential Mode)");
        
        // 1. Stop the Render Loop FIRST to prevent access violations during disposal
        if (_renderControl != null)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] Step 1: Stopping Render Loop...");
            await _renderControl.StopLoopAsync();
        }

        // 2. Dispose of libmpv AFTER rendering is guaranteed to have stopped
        if (Player != null)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] Step 2: Disposing Player...");
            try
            {
                await Player.DisposeAsync();
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] Step 2: Player.DisposeAsync SUCCESS");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] Step 2: Player.DisposeAsync FAILED: {ex.Message}");
            }
            Player = null;
        }

        // 3. Destroy D3D11 Resources LAST
        if (_renderControl != null)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] Step 3: Destroying D3D11 Resources...");
            _renderControl.DestroyResources();
        }

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [LIFECYCLE] CleanupAsync COMPLETED");
    }

    public void SetDisplayFps(double fps)
    {
        try
        {
            Player?.Client.SetProperty("display-fps", fps);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MPV_ERR] Failed to set display-fps to {fps}: {ex.Message}");
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
