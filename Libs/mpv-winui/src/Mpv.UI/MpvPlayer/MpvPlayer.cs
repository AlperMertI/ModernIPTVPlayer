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
        await Task.Run(() => Player.Client.SetProperty(name, valStr));
    }

    public async Task<string> GetPropertyAsync(string name)
    {
        if (Player == null) return "N/A";
        return await Task.Run(() => Player.Client.GetPropertyToString(name));
    }

    public async Task<bool> GetPropertyBoolAsync(string name)
    {
        if (Player == null) return false;
        return await Task.Run(() => Player.Client.GetPropertyToBoolean(name));
    }

    public async Task<long> GetPropertyLongAsync(string name)
    {
        if (Player == null) return -1;
        return await Task.Run(() => Player.Client.GetPropertyToLong(name));
    }

    public async Task ExecuteCommandAsync(params string[] args)
    {
        if (Player == null) return;
        await Player.Client.ExecuteAsync(args);
    }

    public async Task CleanupAsync()
    {
        if (Player != null)
        {
            await Player.DisposeAsync();
            Player = null;
        }
    }

    public void SetDisplayFps(double fps)
    {
        Player?.Client.SetProperty("display-fps", fps);
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
}
