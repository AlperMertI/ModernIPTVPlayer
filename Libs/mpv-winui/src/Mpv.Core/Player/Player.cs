// Copyright (c) Bili Copilot. All rights reserved.

using Mpv.Core.Args;
using Mpv.Core.Interop;
using Mpv.Core.Structs.Render;
using Mpv.Core.Structs.RenderGL;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;

namespace Mpv.Core;

public sealed partial class Player
{
    public Player()
    {
        Client = new MpvClientNative();
        Dependencies = new Structs.Player.LibMpvDependencies();
    }

    public async Task DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Client.UnObserveProperties();
        RenderContext?.Destroy();
        await Client.DestroyAsync();
    }

    public async Task TerminateAsync()
    {
        _isDisposed = true;
        await Client.DestroyAsync(true);
    }

    public async Task InitializeAsync(InitializeArgument? argument = null)
    {
        if (Client.IsInitialized)
        {
            return;
        }

        AutoPlay = argument?.AutoPlay ?? true;
        await Client.InitializeAsync();
        RerunEventLoop();
        if (argument?.ConfigFile is not null)
        {
            await Client.LoadConfigAsync(argument.ConfigFile);
        }

        if (argument?.OpenGLGetProcAddress is not null)
        {
            var glParams = new MpvOpenGLInitParams
            {
                GetProcAddrFn = (ctx, name) =>
                {
                    return argument!.OpenGLGetProcAddress!(name);
                },

                GetProcAddressCtx = IntPtr.Zero
            };

            var glParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(glParams));
            Marshal.StructureToPtr(glParams, glParamsPtr, false);
            var glStringPtr = Marshal.StringToCoTaskMemUTF8("opengl");
            RenderContext = new MpvRenderContextNative(
                Client.Handle,
                [
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.ApiType, Data = glStringPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.OpenGLInitParams, Data = glParamsPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero },
                ]);

            Marshal.FreeHGlobal(glParamsPtr);
            Marshal.FreeCoTaskMem(glStringPtr);
        }
    }

    public async Task InitializeDXGIAsync(IntPtr device, IntPtr context)
    {
        if (Client.IsInitialized)
        {
            return;
        }

        Debug.WriteLine($"[LOG] InitializeDXGIAsync - Using Persistent D3D11 Handles (Dev: {device:X})");
        await Client.InitializeAsync();
        
        // vo=libmpv kullanıyoruz ki MPV bizim SwapChainPanel'imize render etsin
        Client.SetProperty("vo", "libmpv");
        Client.SetProperty("gpu-api", "d3d11");
        Client.SetProperty("hwdec", "d3d11va");
        
        // MPV UI/OSD'yi tamamen kapat - bizim kendi UI'ımız var
        // MPV UI/OSD'yi tamamen kapat - bizim kendi UI'ımız var
        // Client.SetProperty("osc", "no");           // osc özelliği yoksa hata verir, kapattık
        Client.SetProperty("osd-level", "0");      // OSD mesajları kapalı
        Client.SetProperty("input-default-bindings", "no"); // Varsayılan tuş atamaları kapalı
        Client.SetProperty("input-vo-keyboard", "no");      // VO keyboard input kapalı
        
        RerunEventLoop();

        var dxgiStringPtr = Marshal.StringToCoTaskMemUTF8("dxgi");
        var dxgiParamsPtr = Marshal.AllocHGlobal(16);
        Marshal.WriteIntPtr(dxgiParamsPtr, device);
        Marshal.WriteIntPtr(dxgiParamsPtr + 8, context);

        var advControlPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(advControlPtr, 1);

        try {
            RenderContext = new MpvRenderContextNative(
                Client.Handle,
                [
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.ApiType, Data = dxgiStringPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGIInitParams, Data = dxgiParamsPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.AdvancedControl, Data = advControlPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
                ]);
            Debug.WriteLine("[LOG] DXGI RenderContext created SUCCESSFULLY with AdvancedControl!");
        } catch (Exception ex) {
            Debug.WriteLine($"[FATAL] DXGI failed: {ex.Message}");
            throw;
        }

        Marshal.FreeHGlobal(dxgiParamsPtr);
        Marshal.FreeCoTaskMem(dxgiStringPtr);
        Marshal.FreeHGlobal(advControlPtr);
    }

    public void RenderGL(int width, int height, int fboInt)
    {
        var fbo = new MpvOpenGLFBO
        {
            Fbo = fboInt,
            W = width,
            H = height
        };

        var fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf(fbo));
        Marshal.StructureToPtr(fbo, fboPtr, false);

        var flipYPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(flipYPtr, 0);
        RenderContext!.Render([
            new MpvRenderParam {Type=Enums.Render.MpvRenderParamType.Fbo, Data = fboPtr },
            new MpvRenderParam {Type=Enums.Render.MpvRenderParamType.FlipY, Data = flipYPtr },
            new MpvRenderParam {Type=Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero },
            ]);

        Marshal.FreeHGlobal(fboPtr);
        Marshal.FreeHGlobal(flipYPtr);
    }

    public void RenderDXGI(IntPtr texture, int width, int height, bool block = true)
    {
        var fbo = new MpvDxgiFbo
        {
            Texture = texture,
            Width = width,
            Height = height
        };

        var fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf(fbo));
        Marshal.StructureToPtr(fbo, fboPtr, false);

        var blockPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(blockPtr, block ? 1 : 0);

        RenderContext!.Render([
            new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGIFbo, Data = fboPtr },
            new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.BlockForTargetTime, Data = blockPtr },
            new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero },
        ]);

        Marshal.FreeHGlobal(fboPtr);
        Marshal.FreeHGlobal(blockPtr);
    }

    public void RerunEventLoop()
    {
        if (_eventLoopCancellationTokenSource != null)
        {
            _eventLoopCancellationTokenSource.Cancel();
            _eventLoopCancellationTokenSource.Dispose();
        }

        Client.UnObserveProperties();
        _eventLoopCancellationTokenSource = new CancellationTokenSource();
        _eventLoopTask = Task.Run(EventLoop, _eventLoopCancellationTokenSource.Token);
        Client.ObserveProperty(PauseProperty, Enums.Client.MpvFormat.Flag);
        Client.ObserveProperty(DurationProperty, Enums.Client.MpvFormat.Int64);
        Client.ObserveProperty(PositionProperty, Enums.Client.MpvFormat.Int64);
        Client.ObserveProperty(PausedForCacheProperty, Enums.Client.MpvFormat.Flag);
    }

    public bool IsMediaLoaded()
        => _isLoaded && !_isDisposed;

    public async Task ExecuteAfterMediaLoadedAsync(string command)
    {
        if (IsMediaLoaded())
        {
            await Client.ExecuteAsync(command);
        }
    }

    public void Play()
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(PauseProperty, false);
        }
    }

    public void Pause()
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(PauseProperty, true);
        }
    }

    public void Seek(TimeSpan ts)
    {
        var pos = ts.TotalSeconds;
        if (pos >= 0 && pos <= _currentDuration)
        {
            // Seek to the position.
            Client.SetProperty(PositionProperty, pos);
        }
        else if (pos > _currentDuration && _currentDuration > 0)
        {
            Client.SetProperty(PositionProperty, Math.Max(0, _currentDuration - 1));
        }
        else if (pos < 0)
        {
            Client.SetProperty(PositionProperty, 0);
        }
    }

    public void SetSpeed(double rate)
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(SpeedProperty, rate);
        }
    }

    public void SetVolume(int volume)
    {
        if (IsMediaLoaded())
        {
            Client.SetProperty(VolumeProperty, volume);
        }
    }

    public void ResetDuration()
    {
        if (IsMediaLoaded())
        {
            _currentDuration = Client.GetPropertyToLong(DurationProperty);
        }
    }

    public bool IsPaused()
        => Client.GetPropertyToBoolean(PauseProperty);

    public async Task TakeScreenshotAsync(string filePath)
    {
        if (IsMediaLoaded())
        {
            await Client.ExecuteAsync($"screenshot-to-file {filePath}");
        }
    }
}
