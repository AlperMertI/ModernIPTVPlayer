#nullable enable
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mpv.Core;
using Mpv.Core.Args;
using Mpv.Core.Enums.Client;
using Mpv.Core.Enums.Player;
using MpvWinUI.Common;
using OpenTK.Graphics.OpenGL;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinRT;

namespace MpvWinUI;

public sealed partial class MpvPlayer : Control
{
    public MpvPlayer()
    {
        DefaultStyleKey = typeof(MpvPlayer);
    }

    protected override void OnApplyTemplate()
    {
        _renderControl = (RenderControl)GetTemplateChild("RenderControl");
        if (_renderControl != null)
        {
            _renderControl.Setting = new ContextSettings()
            {
                MajorVersion = 4,
                MinorVersion = 6,
                GraphicsProfile = OpenTK.Windowing.Common.ContextProfile.Compatability,
            };
            // _renderControl.Render += OnRender; // Disable UI thread render callback
        }
    }

    private bool _isRenderInitialized = false;

    public async Task InitializePlayerAsync()
    {
        Player ??= new Player();

        if (!Player.Client.IsInitialized)
        {
            // Player.PlaybackPositionChanged += OnPositionChanged; // Removed internal UI update
            // Player.PlaybackStateChanged += OnStateChanged; // Removed internal UI update
            Debug.WriteLine($"[MpvPlayer] Initializing player on thread {Environment.CurrentManagedThreadId}.");
            _renderControl?.Initialize();
            
            if (_renderControl != null)
            {
                // Disable Continuous Rendering to fix Window Dragging Stutter
                _renderControl.ContinuousRendering = false;
            }

            // Critical options that must be set BEFORE initialization
            if (Player?.Client == null) return;
            
            Player.Client.SetProperty("vo", "libmpv");
            Player.Client.SetProperty("hwdec", "auto-safe");
            
            // Switch to ANGLE (OpenGL over DirectX) to fix Intel OSD/Subtitle crashes
            Player.Client.SetProperty("gpu-api", "opengl");
            Player.Client.SetProperty("gpu-context", "angle");

            // Optimize for background rendering (Step 2.5)
            Player.Client.SetProperty("opengl-swapinterval", "0");
            Player.Client.SetProperty("video-sync", "desync");
            Player.Client.SetProperty("opengl-glfinish", "no");
            Player.Client.SetProperty("opengl-waitvsync", "no");

            // Initialization options
            Player.Client.SetOption("ytdl", "no");
            Player.Client.SetOption("user-agent", "IPTVSmartersPlayer");

            // Streaming & Resilience (Fix for stuttering/packet loss)
            Player.Client.SetOption("profile", "fast"); // Recommended by MPV for slow/broken streams
            Player.Client.SetOption("hwdec", "auto"); // 'auto' is often more robust than 'auto-safe' for some drivers
            Player.Client.SetOption("vd-lavc-dr", "yes"); // Direct Rendering
            Player.Client.SetOption("framedrop", "vo"); // Drop frames at VO if too slow, keep audio synced


            Player.Client.RequestLogMessage(MpvLogLevel.Warn);
            Player.LogMessageReceived += OnLogMessageReceived;
            var args = new InitializeArgument(default); // Removed func: RenderContext.GetProcAddress
            await Player.InitializeAsync(args);

            // Hook up the Update Callback - MOVED TO RenderingLoop
            _updateCallback = OnMpvUpdate;
            // Player.RenderContext?.SetUpdateCallback(_updateCallback, IntPtr.Zero);

            // Step 3: Defer all GL work to the background thread.
            // We'll call MakeCurrent() on the background thread.

            // Start background rendering loop (Step 2.1)
            _renderCts = new System.Threading.CancellationTokenSource();
            _renderTask = Task.Run(RenderingLoop, _renderCts.Token);
        }
    }

    public async Task OpenAsync(string url)
    {
        await InitializePlayerAsync();
        if (Player?.Client != null) await Player.Client.ExecuteAsync($"loadfile \"{url}\"");
    }

    public async Task OpenAsync(StorageFile file)
    {
        await InitializePlayerAsync();
        if (Player?.Client != null) await Player.Client.ExecuteAsync($"loadfile \"{file.Path}\"");
    }

    // Removed internal UI handlers
    /*
    private void OnPlayRateSelectionChanged(object sender, SelectionChangedEventArgs e) { ... }
    private void OnSkipBackwardButtonClick(object sender, RoutedEventArgs e) { ... }
    private void OnSkipForwardButtonClick(object sender, RoutedEventArgs e) { ... }
    private void OnPlayPauseButtonClick(object sender, RoutedEventArgs e) { ... }
    */

    private void OnRender(TimeSpan e)
    {
        Render();
    }

    // Removed VisualState updates
    private void OnStateChanged(object sender, PlaybackStateChangedEventArgs e)
    {
        // Internal UI removed, no state to update
    }

    // Removed Position updates
    private void OnPositionChanged(object sender, PlaybackPositionChangedEventArgs e)
    {
        // Internal UI removed, no position block to update
    }

    public void Play() => Player?.Play();

    public void Pause() => Player?.Pause();

    public async Task CleanupAsync()
    {
        _renderCts?.Cancel();
        _renderSignal.Set(); // Wake up loop if waiting
        if (_renderTask != null) await _renderTask;
        _renderCts?.Dispose();
        _renderCts = null;

        if (_renderControl != null) _renderControl.Render -= OnRender;
        if (Player != null)
        {
            Player.Pause();
            await Player.DisposeAsync();
            Player = null;
        }
    }

    public async Task<string> GetPropertyAsync(string propertyName)
    {
        if (Player?.Client?.IsInitialized is not true) return "N/A";
        try
        {
            var result = await Task.Run(() => Player.Client.GetPropertyToString(propertyName));
            return result ?? "N/A";
        }
        catch (Exception)
        {
            // Suppress error and return default
            return "N/A";
        }
    }

    public async Task<long> GetPropertyLongAsync(string propertyName)
    {
        if (Player?.Client?.IsInitialized is not true) return 0;
        try
        {
            // Execute safely inside the task as well
            return await Task.Run(() => 
            {
                try { return Player.Client.GetPropertyToLong(propertyName); }
                catch { return 0; }
            });
        }
        catch
        {
            return 0;
        }
    }

    public async Task<bool> GetPropertyBoolAsync(string propertyName)
    {
        if (Player?.Client?.IsInitialized is not true) return false;
        try
        {
            return await Task.Run(() => 
            {
                try { return Player.Client.GetPropertyToBoolean(propertyName); }
                catch { return false; }
            });
        }
        catch
        {
            return false;
        }
    }

    public async Task SetPropertyAsync(string propertyName, string value)
    {
        if (Player?.Client?.IsInitialized is not true) return;
        try
        {
            await Task.Run(() => Player.Client.SetProperty(propertyName, value));
        }
        catch (Exception)
        {
            throw;
        }
    }

    public async Task ExecuteCommandAsync(params string[] commandArgs)
    {
        if (Player?.Client?.IsInitialized is not true) return;
        try
        {
            await Player.Client.ExecuteWithResultAsync(commandArgs);
        }
        catch (Exception)
        {
            throw new Exception("Komut yürütülürken hata oluştu.");
        }
    }

    private void OnLogMessageReceived(object sender, LogMessageReceivedEventArgs e)
    {
        Debug.WriteLine($"[{e.Level}]\t{e.Prefix}: {e.Message}");
    }


    // Keep delegate alive to prevent GC
    private Mpv.Core.Interop.MpvRenderContextNative.MpvRenderUpdateCallback? _updateCallback;
    
    private readonly System.Threading.AutoResetEvent _renderSignal = new(false);
    private System.Threading.CancellationTokenSource? _renderCts;
    private Task? _renderTask;

    // Lock for background rendering vs UI thread (resize/dispose)
    private readonly object _renderLock = new();
    private readonly PerformanceProfiler _profiler = new();
    private long _frameIdCounter = 0;
    private Task? _pendingPresentTask;

    private void OnMpvUpdate(IntPtr ctx)
    {
        // Debug.WriteLine("[MpvPlayer] OnMpvUpdate signaled.");
        // Signal the background rendering thread
        _renderSignal.Set();
    }

    private unsafe void RenderingLoop()
    {
        Debug.WriteLine($"[MpvPlayer] RenderingLoop started on thread {Environment.CurrentManagedThreadId}.");
        
        // STEP 4 & 5: Attach context and load bindings with retry
        bool attached = false;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                _renderControl?.Context?.GraphicsContext?.MakeCurrent();
                attached = true;
                Debug.WriteLine($"[MpvPlayer] Context attached successfully on thread {Environment.CurrentManagedThreadId} (Attempt {i+1}).");
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MpvPlayer] Context attachment attempt {i+1} failed: {ex.Message}");
                if (i < 4) System.Threading.Thread.Sleep(50 * (i + 1));
            }
        }

        if (attached)
        {
            try
            {
                if (RenderContext.SharedBindingContext != null)
                {
                    GL.LoadBindings(RenderContext.SharedBindingContext);
                    Debug.WriteLine($"[MpvPlayer] GL Bindings loaded on thread {Environment.CurrentManagedThreadId}.");
                }

                // STEP 5: Initialize MPV Render Context on this thread
                if (!_isRenderInitialized && Player != null)
                {
                    Debug.WriteLine("[MpvPlayer] Initializing MPV RenderContext on background thread...");
                    Player.InitializeRender(RenderContext.GetProcAddress);
                    _isRenderInitialized = true;
                    Debug.WriteLine("[MpvPlayer] MPV RenderContext initialization completed.");
                    
                    // STEP 6: Hook up the Update Callback on this thread
                    if (Player.RenderContext != null)
                    {
                        Player.RenderContext.SetUpdateCallback(_updateCallback, IntPtr.Zero);
                        Debug.WriteLine("[MpvPlayer] MPV Update Callback set.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MpvPlayer] Background initialization error: {ex.Message}");
            }
        }
        else
        {
             Debug.WriteLine("[MpvPlayer] FATAL: Failed to attach OpenGL context to rendering thread.");
        }

        while (_renderCts != null && !_renderCts.Token.IsCancellationRequested)
        {
            // Wait for MPV to signal a new frame
            // We use a small timeout to keep the thread alive and check for cancellation
            _profiler.BeginStep("SignalWait");
            bool signaled = _renderSignal.WaitOne(1000);
            _profiler.EndStep("SignalWait");

            if (!signaled)
            {
                 continue;
            }
            
            if (_renderCts == null || _renderCts.Token.IsCancellationRequested)
                break;

            // Debug.WriteLine($"[MpvPlayer] RenderingLoop: Processing frame. FB Ready: {_renderControl?.FrameBuffer != null}");

            // STEP 2.3 & 2.4: Perform rendering and handle resize directly on background thread
            lock (_renderLock)
            {
                if (_renderControl != null)
                {
                    if (_renderControl.BufferNeedsLoading)
                    {
                        _renderControl.CreateFrameBufferOnCurrentThread();
                        var swapChainHandle = _renderControl.FrameBuffer.SwapChainHandle;
                        // Call SetSwapChain on UI thread
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            _renderControl.As<RenderControl>().Content.As<SwapChainPanel>().As<MpvWinUI.Common.ISwapChainPanelNative>().SetSwapChain(swapChainHandle);
                        });
                    }

                    if (_renderControl.ResizeRequested)
                    {
                        Debug.WriteLine($"[MpvPlayer] Resize requested in RenderingLoop. Target: {_renderControl.BufferWidth}x{_renderControl.BufferHeight}");
                        
                        // Phase 13: Synchronize with background presentation before destroying resources
                        if (_pendingPresentTask != null && !_pendingPresentTask.IsCompleted)
                        {
                            PerformanceProfiler.LogEvent("[Resize] Waiting for pending presentation...");
                            _pendingPresentTask.Wait();
                        }
                        
                        _renderControl.UpdateFrameBufferSize();
                    }

                    if (Player?.Client?.IsInitialized is true && _renderControl?.FrameBuffer != null)
                    {
                        try
                        {
                            var frameId = Interlocked.Increment(ref _frameIdCounter);
                            _profiler.BeginFrame();
                            var fb = (FrameBuffer)_renderControl.FrameBuffer;
                            var bufferIndex = (uint)(frameId % 3);

                            
                            _profiler.BeginStep("DXGIWait");
                            fb.WaitForNextBuffer();
                            _profiler.EndStep("DXGIWait");

                            _profiler.BeginStep("DXLock");
                            fb.Lock(bufferIndex);
                            _profiler.EndStep("DXLock");
                            
                            _profiler.BeginStep("GLClear");
                            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                            _profiler.EndStep("GLClear");

                            _profiler.BeginStep("MPVInternal");
                            Player.RenderGL(fb.BufferWidth, fb.BufferHeight, fb.GLFrameBufferHandle);
                            _profiler.EndStep("MPVInternal");

                            _profiler.BeginStep("DXUnlock");
                            fb.Unlock();
                            _profiler.EndStep("DXUnlock");

                            // Phase 13: Move Acquisition to Shadow Task and split profiling
                            var currentFb = fb;
                            var prof = _profiler;
                            var capturedIndex = bufferIndex;

                            _pendingPresentTask = Task.Run(() => 
                            {
                                var sw = Stopwatch.StartNew();
                                try 
                                {
                                    // PerformanceProfiler.LogEvent($"[F:{frameId}|B:{capturedIndex}] Shadow-Task START");
                                    
                                    // Step 1: Acquire BackBuffer (Wait for DXGI rotate)
                                    var backBuffer = currentFb.GetBackBuffer();
                                    if (backBuffer == null) return;
                                    prof.RecordAsyncStep("BufferAcquire", sw.Elapsed.TotalMilliseconds);

                                    // Step 2: Copy Resource (GPU Move)
                                    sw.Restart();
                                    currentFb.CopyResourceToBackBuffer(backBuffer, capturedIndex);
                                    prof.RecordAsyncStep("DXCopy", sw.Elapsed.TotalMilliseconds);

                                    // Step 3: Present (DWM Submission)
                                    sw.Restart();
                                    currentFb.SubmitPresent();
                                    prof.RecordAsyncStep("DXPresent", sw.Elapsed.TotalMilliseconds);

                                    backBuffer->Release();
                                    // PerformanceProfiler.LogEvent($"[F:{frameId}|B:{capturedIndex}] Shadow-Task FINISH");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[MpvPlayer] Shadow task error: {ex.Message}");
                                }
                            });

                            _profiler.EndFrame();
                            // Debug.WriteLine($"[MpvPlayer] Frame rendered ({fb.BufferWidth}x{fb.BufferHeight}) and presented.");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[MpvPlayer] RenderingLoop error: {ex.Message}");
                        }
                    }
                    else
                    {
                        // Debug.WriteLine($"[MpvPlayer] Rendering skipped. Player init: {Player?.Client?.IsInitialized}, FB: {_renderControl?.FrameBuffer != null}");
                    }
                }
            }
        }
        
        // Phase 13: Ensure background tasks finish before detaching context
        if (_pendingPresentTask != null && !_pendingPresentTask.IsCompleted)
        {
            try { _pendingPresentTask.Wait(500); } catch { }
        }

        // Detach when done
        try { RenderContext.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); } catch { } 
    }

    private void Render()
    {
        // This method is now unused for background rendering, but kept for compatibility or manual triggers
        return;
    }
}
