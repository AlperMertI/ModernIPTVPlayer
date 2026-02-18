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

    public async Task InitializeDXGIAsync(IntPtr device, IntPtr context, string api = "dxgi")
    {
        if (Client.IsInitialized)
        {
            return;
        }

        Debug.WriteLine($"[LOG] InitializeAsync (API: {api}) - Using Persistent D3D11 Handles (Dev: {device:X})");
        
        // CrITICAL: Set options BEFORE initialization to ensure scripts and OSD load correctly.
        // Enable built-in scripts (osc, stats) via explicit loading.
        // Must use SetOption with correct types (bool/long) for pre-init configuration!
        Client.SetOption("load-scripts", true);
        Client.SetOption("input-default-bindings", true);
        Client.SetOption("input-vo-keyboard", false); // We handle keyboard input in WinUI
        
        // Increase log level to debug script loading issues
        Client.RequestLogMessage(MpvLogLevel.Info);

        await Client.InitializeAsync();
        
        Client.SetProperty("vo", "libmpv");
        Client.SetProperty("gpu-api", "d3d11");

        // Enable OSD level 1 for stats/osc visibility (Must be set as property AFTER init)
        Client.SetProperty("osd-level", 1L); 

        // Load scripts manually using robust command execution
        // We use ExecuteWithResultAsync with an array to ensure paths with spaces are handled correctly by libmpv
        try
        {
            var appPath = AppDomain.CurrentDomain.BaseDirectory;
            var scriptDir = System.IO.Path.Combine(appPath, "scripts");
            
            // Normalize paths to forward slashes for MPV compatibility
            var statsPath = System.IO.Path.Combine(scriptDir, "stats.lua").Replace("\\", "/");
            var oscPath = System.IO.Path.Combine(scriptDir, "osc.lua").Replace("\\", "/");
            
            Debug.WriteLine($"[MpvPlayer] Loading scripts from: {scriptDir}");
            Debug.WriteLine($"[MpvPlayer] Stats Path: {statsPath}");

            if (System.IO.File.Exists(statsPath))
            {
                try 
                {
                    Debug.WriteLine($"[MpvPlayer] Loading stats.lua...");
                    await Client.ExecuteWithResultAsync(new[] { "load-script", statsPath });
                    Debug.WriteLine($"[MpvPlayer] stats.lua loaded successfully.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MpvPlayer] ERROR loading stats.lua: {ex.Message}");
                }
            }
            else
            {
                 Debug.WriteLine($"[MpvPlayer] stats.lua NOT FOUND at {statsPath}");
            }

            if (System.IO.File.Exists(oscPath))
            {
                try
                {
                    Debug.WriteLine($"[MpvPlayer] Loading osc.lua...");
                    await Client.ExecuteWithResultAsync(new[] { "load-script", oscPath });
                    Debug.WriteLine($"[MpvPlayer] osc.lua loaded successfully.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MpvPlayer] ERROR loading osc.lua: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MpvPlayer] General Script loading error: {ex}");
        }
        
        RerunEventLoop();

        var apiStringPtr = Marshal.StringToCoTaskMemUTF8(api);
        var dxgiParamsPtr = Marshal.AllocHGlobal(16);
        Marshal.WriteIntPtr(dxgiParamsPtr, device);
        Marshal.WriteIntPtr(dxgiParamsPtr + 8, context);

        var advControlPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(advControlPtr, 1);
        
        var d3d11DevPtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(d3d11DevPtr, device);

        try {
            RenderContext = new MpvRenderContextNative(
                Client.Handle,
                [
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.ApiType, Data = apiStringPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.DXGIInitParams, Data = dxgiParamsPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.AdvancedControl, Data = advControlPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.D3D11Device, Data = d3d11DevPtr },
                    new MpvRenderParam { Type = Enums.Render.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
                ]);
            Debug.WriteLine($"[LOG] {api} RenderContext created SUCCESSFULLY!");
        } catch (Exception ex) {
            Debug.WriteLine($"[FATAL] {api} failed: {ex.Message}");
            throw;
        }

        Marshal.FreeHGlobal(dxgiParamsPtr);
        Marshal.FreeCoTaskMem(apiStringPtr);
        Marshal.FreeHGlobal(advControlPtr);
        Marshal.FreeHGlobal(d3d11DevPtr);
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
