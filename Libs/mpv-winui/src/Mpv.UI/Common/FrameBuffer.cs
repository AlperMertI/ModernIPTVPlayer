using Microsoft.UI.Xaml.Media;
using Silk.NET.DXGI;
using OpenTK.Graphics.OpenGL;
using OpenTK.Graphics.Wgl;
using System;
using Silk.NET.Core.Native;
using OpenTK.Platform.Windows;
using Silk.NET.Direct3D11;
using System.Reflection;
using System.Diagnostics;

namespace MpvWinUI.Common;

public unsafe class FrameBuffer : FrameBufferBase
{
    public RenderContext Context { get; }


    public int GLDepthRenderBufferHandle { get; set; }

    public int GLFrameBufferHandle => (int)_glColorFrameBufferHandles[_currentIndex];

    private const int BufferCount = 3;
    private void* _frameWaitableObject;
    private uint _currentIndex = 0;
    public uint CurrentBufferIndex => _currentIndex;
    
    // Private textures for triple buffering (bypasses SwapChainForComposition limitations)
    private readonly ID3D11Texture2D*[] _privateTextures = new ID3D11Texture2D*[BufferCount];
    private readonly nint[] _dxInteropColorHandles = new nint[BufferCount];
    private readonly uint[] _glColorRenderBufferHandles = new uint[BufferCount];
    private readonly uint[] _glColorFrameBufferHandles = new uint[BufferCount];
    private ID3D11Multithread* _dxMultithread;
    public override int BufferWidth { get; protected set; }
    public override int BufferHeight { get; protected set; }
    public override nint SwapChainHandle { get; protected set; }
    
    // Cached interfaces
    private IDXGISwapChain1* _swapChain1;
    private IDXGISwapChain2* _swapChain2;
    // We don't need SC3 anymore as we manage indices manually for private textures
    
    public IntPtr DxInteropColorHandle => (IntPtr)_dxInteropColorHandles[_currentIndex];

    public FrameBuffer(
        RenderContext context,
        int frameBufferWidth,
        int frameBufferHeight,
        double compositionScaleX,
        double compositionScaleY)
    {
        Context = context;
        BufferWidth = Convert.ToInt32(frameBufferWidth * compositionScaleX);
        BufferHeight = Convert.ToInt32(frameBufferHeight * compositionScaleY);


        // SwapChain
        {
            SwapChainDesc1 swapChainDesc = new()
            {
                Width = (uint)BufferWidth,
                Height = (uint)BufferHeight,
                Format = Format.FormatB8G8R8A8Unorm,
                Stereo = 0,
                SampleDesc = new SampleDesc()
                {
                    Count = 1,
                    Quality = 0
                },
                BufferUsage = DXGI.UsageRenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                Flags = (uint)(SwapChainFlag.FrameLatencyWaitableObject | SwapChainFlag.AllowTearing),
                AlphaMode = AlphaMode.Ignore,
            };

            IDXGISwapChain1* swapChain = null;
            var hr = ((IDXGIFactory2*)Context.DxDeviceFactory)->CreateSwapChainForComposition((IUnknown*)Context.DxDeviceHandle, &swapChainDesc, null, &swapChain);
            if (hr != 0) throw new Exception($"CreateSwapChainForComposition failed with HR: 0x{hr:X}");

            SwapChainHandle = (IntPtr)swapChain;
            _swapChain1 = swapChain;
            
            // Query for extended interfaces
            void* ptr2 = null;
            Guid guid2 = typeof(IDXGISwapChain2).GetTypeInfo().GUID;
            if (swapChain->QueryInterface(&guid2, &ptr2) == 0)
            {
                _swapChain2 = (IDXGISwapChain2*)ptr2;
                _frameWaitableObject = _swapChain2->GetFrameLatencyWaitableObject();
                // Set latency for smoother performance with 3 buffers
                _swapChain2->SetMaximumFrameLatency(2);
                
                var matrix = new Matrix3X2F { DXGI11 = 1.0f / (float)compositionScaleX, DXGI22 = 1.0f / (float)compositionScaleY };
                _swapChain2->SetMatrixTransform(&matrix);
            }
            
            Debug.WriteLine($"[FrameBuffer] SwapChain created (Waitable: {_frameWaitableObject != null})");
            
            // Enable D3D11 Multithreading for background Copy/Present
            void* mtPtr = null;
            Guid mtGuid = typeof(ID3D11Multithread).GetTypeInfo().GUID;
            if (((IUnknown*)Context.DxDeviceHandle)->QueryInterface(&mtGuid, &mtPtr) == 0)
            {
                _dxMultithread = (ID3D11Multithread*)mtPtr;
                _dxMultithread->SetMultithreadProtected(1);
                Debug.WriteLine("[FrameBuffer] D3D11 Multithread Protection enabled.");
            }
        }

        GLDepthRenderBufferHandle = GL.GenRenderbuffer();
        
        CreateRenderBuffers();
    }

    private void CreateRenderBuffers()
    {
        if (_swapChain1 == null) return;
        
        Guid guid = typeof(ID3D11Texture2D).GetTypeInfo().GUID;
        ID3D11Device* device = (ID3D11Device*)Context.DxDeviceHandle;
        int hr;

        // 1. We no longer cache _swapChainBuffer here to ensure we get the fresh back-buffer every frame in Present()

        // 2. Create 3 private textures for MPV to render into
        Texture2DDesc desc = new()
        {
            Width = (uint)BufferWidth,
            Height = (uint)BufferHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatB8G8R8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.RenderTarget,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };

        // Allocate storage for Shared Depth Buffer
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, (uint)GLDepthRenderBufferHandle);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, BufferWidth, BufferHeight);

        for (uint i = 0; i < BufferCount; i++)
        {
            ID3D11Texture2D* privateTex = null;
            hr = device->CreateTexture2D(&desc, null, &privateTex);
            
            if (hr == 0 && privateTex != null)
            {
                _privateTextures[i] = privateTex;
                uint glRb = (uint)GL.GenRenderbuffer();
                _glColorRenderBufferHandles[i] = glRb;
                
                // Use AccessWriteOnly for private buffers to optimize driver path
                _dxInteropColorHandles[i] = Wgl.DXRegisterObjectNV(Context.GlDeviceHandle, (nint)privateTex, glRb, (uint)RenderbufferTarget.Renderbuffer, (WGL_NV_DX_interop)0x0002); // 0x0002 = AccessWriteOnly
                
                Debug.WriteLine($"[FrameBuffer] Registered Private Buffer {i} (RB: {glRb})");

                // Pre-create and attach FBO for this buffer (Pooling)
                uint fbo = (uint)GL.GenFramebuffer();
                _glColorFrameBufferHandles[i] = fbo;
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, glRb);
                
                // Share the same depth buffer across all FBOs
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, (uint)GLDepthRenderBufferHandle);
                
                CheckFramebufferStatus($"FBO_{i}");
            }
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Phase 14: Ensure all registration commands are finished before the first Lock
        GL.Finish();
    }

    private void CheckFramebufferStatus(string label)
    {
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            Debug.WriteLine($"[FrameBuffer] Error: Framebuffer {label} is incomplete: {status}");
        }
    }
    
    private void DestroyRenderBuffers()
    {
        for (int i = 0; i < BufferCount; i++)
        {
            if (_dxInteropColorHandles[i] != IntPtr.Zero)
            {
                Wgl.DXUnregisterObjectNV(Context.GlDeviceHandle, _dxInteropColorHandles[i]);
                _dxInteropColorHandles[i] = IntPtr.Zero;
            }
            
            if (_glColorRenderBufferHandles[i] != 0)
            {
                GL.DeleteRenderbuffer(_glColorRenderBufferHandles[i]);
                _glColorRenderBufferHandles[i] = 0;
            }

            if (_privateTextures[i] != null)
            {
                _privateTextures[i]->Release();
                _privateTextures[i] = null;
            }

            if (_glColorFrameBufferHandles[i] != 0)
            {
                GL.DeleteFramebuffer(_glColorFrameBufferHandles[i]);
                _glColorFrameBufferHandles[i] = 0;
            }
        }

        if (_dxMultithread != null)
        {
            _dxMultithread->Release();
            _dxMultithread = null;
        }
    }

    public void WaitForNextBuffer()
    {
        if (_frameWaitableObject != null)
        {
            RenderContext.WaitForSingleObject((IntPtr)_frameWaitableObject, 100);
        }
    }

    public void Lock(uint index)
    {
        _currentIndex = index;

        nint handle = _dxInteropColorHandles[_currentIndex];

        if (handle == IntPtr.Zero)
        {
            Debug.WriteLine($"[FrameBuffer] Error: Interop handle for index {_currentIndex} is null. Cannot lock.");
            return;
        }

        // Phase 14: Sync GL state before locking to ensure driver is ready
        // GL.Finish(); // REMOVED: Performance Optimization
        
        bool success = Wgl.DXLockObjectsNV(Context.GlDeviceHandle, 1, new[] { handle });
        if (!success)
        {
            Debug.WriteLine($"[FrameBuffer] Error: DXLockObjectsNV failed for index {_currentIndex}");
        }
        
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)GLFrameBufferHandle);

        // Phase 14 Fix: Explicitly set draw buffer and check status to prevent InvalidOperation on GL.Clear
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        
        if (_currentIndex == 0) CheckFramebufferStatus($"Lock_Index0");

        GL.Viewport(0, 0, BufferWidth, BufferHeight);
    }

    public void Begin(uint index)
    {
        WaitForNextBuffer();
        Lock(index);
    }

    public void Unlock()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        
        nint handle = _dxInteropColorHandles[_currentIndex];
        if (handle != IntPtr.Zero)
        {
            Wgl.DXUnlockObjectsNV(Context.GlDeviceHandle, 1, new[] { handle });
        }
    }

    public ID3D11Texture2D* GetBackBuffer()
    {
        if (_swapChain1 == null) return null;
        
        ID3D11Texture2D* backBuffer = null;
        Guid guid = typeof(ID3D11Texture2D).GetTypeInfo().GUID;
        var hr = _swapChain1->GetBuffer(0, &guid, (void**)&backBuffer);
        return (hr == 0) ? backBuffer : null;
    }

    public void CopyResourceToBackBuffer(ID3D11Texture2D* backBuffer, uint index)
    {
        if (_swapChain1 == null || backBuffer == null || index >= BufferCount) return;
        if (_privateTextures[index] == null) return;

        var context = (ID3D11DeviceContext*)Context.DxDeviceContext;
        lock (Context) // Extra safety for D3D11 Context access if not already protected
        {
            context->CopyResource((ID3D11Resource*)backBuffer, (ID3D11Resource*)_privateTextures[index]);
        }
    }

    public void SubmitPresent()
    {
        if (_swapChain1 == null) return;
        _swapChain1->Present(0, 0);
    }

    public void CheckGLError(string location)
    {
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Debug.WriteLine($"[FrameBuffer] GL Error at {location}: {error}");
        }
    }

    public void End()
    {
        Unlock();
        ID3D11Texture2D* backBuffer = GetBackBuffer();
        if (backBuffer != null)
        {
            CopyResourceToBackBuffer(backBuffer, _currentIndex);
            SubmitPresent();
            backBuffer->Release();
        }
    }

    public void UpdateSize(
        int framebufferWidth,
        int framebufferHeight,
        double compositionScaleX,
        double compositionScaleY)
    {
        BufferWidth = Convert.ToInt32(framebufferWidth * compositionScaleX);
        BufferHeight = Convert.ToInt32(framebufferHeight * compositionScaleY);

        DestroyRenderBuffers();

        // FLAGS MUST MATCH CONSTRUCTOR EXACTLY (SwapChainFlag.FrameLatencyWaitableObject | SwapChainFlag.AllowTearing)
        var hr = _swapChain1->ResizeBuffers(BufferCount, (uint)BufferWidth, (uint)BufferHeight, Format.FormatUnknown, (uint)(SwapChainFlag.FrameLatencyWaitableObject | SwapChainFlag.AllowTearing));
        if (hr != 0) Debug.WriteLine($"[FrameBuffer] ResizeBuffers failed with HR: 0x{hr:X}");

        if (_swapChain2 != null)
        {
            var matrix = new Matrix3X2F { DXGI11 = 1.0f / (float)compositionScaleX, DXGI22 = 1.0f / (float)compositionScaleY };
            _swapChain2->SetMatrixTransform(&matrix);
        }
        
        CreateRenderBuffers();
    }

    public override void Dispose()
    {
        DestroyRenderBuffers();
        GL.DeleteRenderbuffer(GLDepthRenderBufferHandle);
        GL.DeleteFramebuffer(GLFrameBufferHandle);

        if (_swapChain2 != null) { /* Release pointers if necessary */ }

        GC.SuppressFinalize(this);
    }
}
