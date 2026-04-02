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

        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] DisposeAsync STARTED");
        _isDisposed = true;

        // 1. Signal event loop to wake up and exit
        try { Client.Wakeup(); } catch { }

        // 2. Wait for event loop task to finish
        if (_eventLoopTask != null)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] Awaiting EventLoop exit...");
            try 
            {
                // We give the event loop a small window to exit gracefully
                var timeoutTask = Task.Delay(1000);
                var completedTask = await Task.WhenAny(_eventLoopTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] WARNING: EventLoop exit timed out.");
                }
                else
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] EventLoop exit CONFIRMED.");
                }
            } 
            catch (Exception ex) 
            { 
                Debug.WriteLine($"[CORE_PLAYER] EventLoop Await Error: {ex.Message}");
            }
        }

        // 3. Clean up native resources
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] Step 3: UnObserving properties...");
        try { Client.UnObserveProperties(); } catch (Exception ex) { Debug.WriteLine($"[CORE_PLAYER] UnObserve FAILED: {ex.Message}"); }
        
        if (RenderContext != null)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] Step 4: Destroying RenderContext...");
            try 
            { 
                RenderContext.Destroy(); 
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] Step 4: RenderContext.Destroy SUCCESS");
            } 
            catch (Exception ex) 
            { 
                Debug.WriteLine($"[CORE_PLAYER] RenderContext.Destroy FAILED: {ex.Message}"); 
            }
        }
        
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] Step 5: Calling Client.DestroyAsync...");
        await Client.DestroyAsync();
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CORE_PLAYER] DisposeAsync COMPLETED");
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

        Debug.WriteLine($"[LOG] InitializeAsync (API: {api}) - Using Persistent D3D11 Handles (Dev: {device:X})");
        
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
        Client.SetOption("d3d11-adapter", adapterName ?? "auto");
        Client.SetOption("d3d11-output-format", "rgba16f");
        Client.SetOption("d3d11-flip", "yes");
        Client.SetOption("d3d11-feature-level", "11_1");
        
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
                    await Client.ExecuteWithResultAsync(new[] { "load-script", statsPath });
                }

                if (System.IO.File.Exists(oscPath))
                {
                    Debug.WriteLine($"[MpvPlayer] Loading osc.lua...");
                    await Client.ExecuteWithResultAsync(new[] { "load-script", oscPath });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MpvPlayer] Script loading error: {ex}");
            }
        }
        
        RerunEventLoop();
        RerunEventLoop();

        // [STRICT_RENDERER_INIT] 
        // We must strictly separate legacy (dxgi) and modern (gpu-next/d3d11) params.
        // Modern (gpu-next) API Name = "d3d11" (per libmpv_d3d11.c:273)
        // Legacy (vo_gpu) API Name = "dxgi" (per libmpv_d3d11.c:83)
        var parameters = new System.Collections.Generic.List<MpvRenderParam>();
        
        // Strict mapping: anything other than "dxgi" is treated as modern (forced).
        bool isModern = api != "dxgi";
        string internalApiName = isModern ? "d3d11" : "dxgi";

        var apiStringPtr = Marshal.StringToCoTaskMemUTF8(internalApiName);
        var advControlPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(advControlPtr, 1); 

        parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.ApiType, Data = apiStringPtr });
        parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.AdvancedControl, Data = advControlPtr });

        IntPtr dxgiParamsPtr = IntPtr.Zero;
        IntPtr d3d11DevPtr = IntPtr.Zero;
        IntPtr d3d11CtxPtr = IntPtr.Zero;

        if (isModern)
        {
            // Modern Path (gpu-next/libplacebo)
            // PASS ID3D11Device* (Type 24) and ID3D11DeviceContext* (Type 25) directly.
            parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.D3D11Device, Data = device });
            parameters.Add(new MpvRenderParam { Type = (Enums.Render.MpvRenderParamType)25, Data = context });
            
            // Pass DXGI colorspace for proper HDR tone mapping (Type 27)
            var cspPtr = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(cspPtr, colorspace);
            parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGIColorspace, Data = cspPtr });
            
            Debug.WriteLine($"[MpvCore] Renderer Init: FORCING MODERN (gpu-next) via '{internalApiName}' | DevPtr: {device:X} | Colorspace: {colorspace}");
        }
        else
        {
            // Legacy Path (vo_gpu/dxgi)
            // MUST pass mpv_dxgi_init_params* (Type 21).
            dxgiParamsPtr = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(dxgiParamsPtr, device);
            Marshal.WriteIntPtr(dxgiParamsPtr + 8, context);
            parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGIInitParams, Data = dxgiParamsPtr });
            Debug.WriteLine($"[MpvCore] Renderer Init: USING LEGACY (vo_gpu/dxgi) via '{internalApiName}'");
        }

        parameters.Add(new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero });

        try {
            // [DETECTIVE_MODE] 
            // Force gpu-api to d3d11 before initializing the render context.
            if (isModern)
            {
                Client.SetOption("gpu-api", "d3d11"); 
                Client.SetOption("gpu-context", "d3d11");
            }
            
            RenderContext = new MpvRenderContextNative(Client.Handle, parameters.ToArray());
            
            // Post-Init Check
            var actualVo = Client.GetPropertyToString("vo");
            Debug.WriteLine($"[DETECTIVE] RenderContext created for {internalApiName}. Actual Core VO: {actualVo}");
            
            if (isModern && actualVo != "libmpv") {
                 Debug.WriteLine($"[DETECTIVE_WARNING] Modern renderer (gpu-next) might have failed! core vo is {actualVo}");
            }
        } catch (Exception ex) {
            Debug.WriteLine($"[FATAL] {internalApiName} failed: {ex.Message}");
            throw;
        } finally {
            // Cleanup native memory
            Marshal.FreeCoTaskMem(apiStringPtr);
            Marshal.FreeHGlobal(advControlPtr);
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
        
        try
        {
            System.IO.File.AppendAllText("C:\\Users\\ASUS\\Documents\\ModernIPTVPlayer\\cs_debug.log", 
                $"[{DateTime.Now:HH:mm:ss.fff}] [CS-RENDER] Tex={texture} | W={width} | H={height} | RW={renderWidth} | RH={renderHeight}\n");
        }
        catch { }

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
