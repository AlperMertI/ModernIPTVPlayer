// Copyright (c) Bili Copilot. All rights reserved.

using Mpv.Core.Args;
using Mpv.Core.Interop;
using Mpv.Core.Structs.Render;
using Mpv.Core.Structs.RenderGL;
using Mpv.Core.Enums.Client;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;

namespace Mpv.Core;

public sealed partial class Player
{
    ~Player()
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [FINALIZER] Mpv.Core.Player finalizing");
    }

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

        // Stop playback and FORCE detach from the video output to release driver threads.
        // This is a critical step to ensure the D3D11 device reference count drops to 0.
        try { await Client.ExecuteAsync(new[] { "set", "vo", "null" }); } catch { }
        try { await Client.ExecuteAsync("stop"); } catch { }
        try { await Client.ExecuteAsync("playlist-clear"); } catch { }
        try { Client.SetProperty("cache", false); } catch { }

        _isDisposed = true;
        try { _eventLoopCancellationTokenSource?.Cancel(); } catch { }

        // 1. Wake up event loop so it exits promptly.
        try { Client.Wakeup(); } catch { }

        // 2. Wait for event loop task to finish.
        if (_eventLoopTask != null)
        {
            try 
            {
                var timeoutTask = Task.Delay(1000);
                await Task.WhenAny(_eventLoopTask, timeoutTask);
            } 
            catch { }
        }

        try { _eventLoopCancellationTokenSource?.Dispose(); } catch { }
        _eventLoopCancellationTokenSource = null;
        _eventLoopTask = null;

        // 3. Clean up native resources.
        try { Client.UnObserveProperties(); } catch { }
        
        // [GRACEFUL_QUIT] Tell libmpv to stop internal threads before we force destroy it
        try { await Client.ExecuteAsync("quit"); } catch { }
        
        // [DRAIN_EVENTS] Flush any remaining native events
        try { for(int i=0; i<5; i++) Client.WaitEvent(0); } catch { }

        if (RenderContext != null)
        {
            try 
            { 
#if DEBUG
                Debug.WriteLine("[PLAYER] Awaiting RenderContext destruction...");
#endif
                await RenderContext.DestroyAsync(); 
#if DEBUG
                Debug.WriteLine("[PLAYER] RenderContext destroyed.");
#endif
                RenderContext = null;
                LogCoreMemory("dispose.after-render-context-destroy");
            } 
            catch (Exception ex)
            {
                Debug.WriteLine($"[PLAYER] RenderContext destruction error: {ex.Message}");
            }
        }
        
        await Client.DestroyAsync();
        LogCoreMemory("dispose.after-client-destroy");
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
            var apiString = argument?.RenderApi == "d3d11" ? "d3d11" : "dxgi";
            var apiStringPtr = Marshal.StringToCoTaskMemUTF8(apiString);
            
            RenderContext = new MpvRenderContextNative(
                Client.Handle,
                [
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.ApiType, Data = apiStringPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.OpenGLInitParams, Data = glParamsPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero },
                ]);

            Marshal.FreeHGlobal(glParamsPtr);
            Marshal.FreeCoTaskMem(apiStringPtr);
        }
    }

    public async Task InitializeDXGIAsync(IntPtr device, IntPtr context, string adapterName = "auto", string api = "dxgi", bool skipScripts = false, int colorspace = 0)
    {
        if (Client.IsInitialized)
        {
            return;
        }
        
        // 1. Core Pre-Init Options
        Client.SetOption("load-scripts", !skipScripts);
        Client.SetOption("input-default-bindings", !skipScripts);
        Client.SetOption("input-vo-keyboard", false); 
        Client.RequestLogMessage(MpvLogLevel.V);
        
        // [FIX] Force correctly selected renderer (gpu-next vs gpu)
        string voName = api == "d3d11" ? "gpu-next" : "gpu";
        Client.SetOption("vo", voName);
        
        Client.SetOption("gpu-api", "d3d11");
        Client.SetOption("gpu-context", "d3d11");
        Client.SetOption("d3d11-output-mode", "composition");
        if (!string.IsNullOrEmpty(adapterName) && adapterName != "auto")
        {
            Client.SetOption("d3d11-adapter", adapterName);
        }
        Client.SetOption("d3d11-flip", "yes");
        
        await Client.InitializeAsync();
        
        // Necessary for libmpv-based render contexts
        Client.SetProperty("vo", "libmpv");
        Client.SetProperty("osd-level", 1L); 

        // 2. Script Loading (Phase 2 - Persistent players only)
        if (!skipScripts)
        {
            try
            {
                var appPath = AppDomain.CurrentDomain.BaseDirectory;
                var scriptDir = System.IO.Path.Combine(appPath, "scripts");
                var statsPath = System.IO.Path.Combine(scriptDir, "stats.lua").Replace("\\", "/");
                var oscPath = System.IO.Path.Combine(scriptDir, "osc.lua").Replace("\\", "/");

                if (System.IO.File.Exists(statsPath))
                {
                    Debug.WriteLine($"[MpvPlayer] Loading stats.lua...");
                    await Client.ExecuteAsync(new[] { "load-script", statsPath });
                }

                if (System.IO.File.Exists(oscPath))
                {
                    Debug.WriteLine($"[MpvPlayer] Loading osc.lua...");
                    await Client.ExecuteAsync(new[] { "load-script", oscPath });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MpvPlayer] Script loading error: {ex}");
            }
        }
        
        RerunEventLoop();

        // [STRICT_RENDERER_INIT] 
        // We must strictly separate legacy (dxgi) and modern (gpu-next/d3d11) params.
        // Modern (gpu-next) API Name = "d3d11" (per libmpv_d3d11.c:273)
        // Legacy (vo_gpu) API Name = "dxgi" (per libmpv_d3d11.c:83)
        var parameters = new System.Collections.Generic.List<MpvRenderParam>();
        
        // Strict mapping: anything other than "dxgi" is treated as modern (forced).
        bool isModern = api != "dxgi";
        string internalApiName = isModern ? "d3d11" : "dxgi";

        IntPtr apiStringPtr = IntPtr.Zero;
        IntPtr advControlPtr = IntPtr.Zero;
        IntPtr cspPtr = IntPtr.Zero;
        IntPtr dxgiParamsPtr = IntPtr.Zero;

        try {
            apiStringPtr = Marshal.StringToCoTaskMemUTF8(internalApiName);
            advControlPtr = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(advControlPtr, 1); 

            parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.ApiType, Data = apiStringPtr });
            parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.AdvancedControl, Data = advControlPtr });

            if (isModern)
            {
                // Modern Path (gpu-next/libplacebo)
                // PASS ID3D11Device* (Type 24) and ID3D11DeviceContext* (Type 25) directly.
                parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.D3D11Device, Data = device });
                parameters.Add(new MpvRenderParam { Type = (Enums.Render.MpvRenderParamType)25, Data = context });
                
                // Pass DXGI colorspace for proper HDR tone mapping (Type 27)
                cspPtr = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(cspPtr, colorspace);
                parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGIColorspace, Data = cspPtr });
            }
            else
            {
                // Legacy Path (vo_gpu/dxgi)
                // MUST pass mpv_dxgi_init_params* (Type 21).
                dxgiParamsPtr = Marshal.AllocHGlobal(16);
                Marshal.WriteIntPtr(dxgiParamsPtr, device);
                Marshal.WriteIntPtr(dxgiParamsPtr + 8, context);
                parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGIInitParams, Data = dxgiParamsPtr });
            }

            parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero });

            // [DETECTIVE_MODE] 
            // Force gpu-api to d3d11 before initializing the render context.
            if (isModern)
            {
                Client.SetOption("gpu-api", "d3d11"); 
                Client.SetOption("gpu-context", "d3d11");
            }
            
            RenderContext = new MpvRenderContextNative(Client.Handle, parameters.ToArray());
        } catch (Exception ex) {
            Debug.WriteLine($"[FATAL] {internalApiName} failed: {ex.Message}");
            throw;
        } finally {
            // Cleanup native memory
            if (apiStringPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(apiStringPtr);
            if (advControlPtr != IntPtr.Zero) Marshal.FreeHGlobal(advControlPtr);
            if (cspPtr != IntPtr.Zero) Marshal.FreeHGlobal(cspPtr);
            if (dxgiParamsPtr != IntPtr.Zero) Marshal.FreeHGlobal(dxgiParamsPtr);
        }
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

    public void RenderDXGI(IntPtr texture, int width, int height, int renderWidth = 0, int renderHeight = 0, bool block = true)
    {
        var fbo = new MpvDxgiFbo
        {
            Texture = texture,
            Width = width,
            Height = height,
            RenderWidth = renderWidth,
            RenderHeight = renderHeight
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

    public void RenderDXGIShared(IntPtr sharedHandle, bool block = true)
    {
        var blockPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(blockPtr, block ? 1 : 0);

        RenderContext!.Render([
            new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGISharedTexture, Data = sharedHandle },
            new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.BlockForTargetTime, Data = blockPtr },
            new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero },
        ]);

        Marshal.FreeHGlobal(blockPtr);
    }

    private static int _rerunCount = 0;
    public void RerunEventLoop()
    {
        if (_eventLoopTask != null && !_eventLoopTask.IsCompleted)
        {
            Client.UnObserveProperties();
            Client.ObserveProperty(PauseProperty, Enums.Client.MpvFormat.Flag);
            Client.ObserveProperty(DurationProperty, Enums.Client.MpvFormat.Int64);
            Client.ObserveProperty(PositionProperty, Enums.Client.MpvFormat.Int64);
            Client.ObserveProperty(PausedForCacheProperty, Enums.Client.MpvFormat.Flag);
            return;
        }

        _eventLoopCancellationTokenSource?.Dispose();
        Client.UnObserveProperties();
        _eventLoopCancellationTokenSource = new CancellationTokenSource();
        var token = _eventLoopCancellationTokenSource.Token;
        _eventLoopTask = Task.Run(() => EventLoop(token), token);
        Client.ObserveProperty(PauseProperty, Enums.Client.MpvFormat.Flag);
        Client.ObserveProperty(DurationProperty, Enums.Client.MpvFormat.Int64);
        Client.ObserveProperty(PositionProperty, Enums.Client.MpvFormat.Int64);
        Client.ObserveProperty(PausedForCacheProperty, Enums.Client.MpvFormat.Flag);
    }

    private static void LogCoreMemory(string stage)
    {
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
