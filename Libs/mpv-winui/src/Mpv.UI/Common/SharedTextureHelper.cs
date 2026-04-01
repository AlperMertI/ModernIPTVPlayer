using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MpvWinUI.Common;

/// <summary>
/// Helper class for creating and managing shared D3D11 textures with keyed mutex synchronization.
/// Uses P/Invoke to call native D3D11 functions directly.
/// </summary>
public class SharedTextureHelper : IDisposable
{
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _sharedTexture = IntPtr.Zero;
    private IntPtr _sharedHandle = IntPtr.Zero;
    private ulong _mutexKey = 0;
    private bool _disposed = false;
    
    private int _width;
    private int _height;
    
    public IntPtr SharedHandle => _sharedHandle;
    public IntPtr SharedTexturePtr => _sharedTexture;
    public bool IsReady => _sharedTexture != IntPtr.Zero && _sharedHandle != IntPtr.Zero;
    public int Width => _width;
    public int Height => _height;
    
    // D3D11 Constants
    private const uint D3D11_BIND_RENDER_TARGET = 0x20;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x08;
    private const uint D3D11_RESOURCE_MISC_SHARED = 0x02;
    private const uint D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX = 0x04;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint DXGI_FORMAT_R10G10B10A2_UNORM = 24;
    
    // DXGI GUIDs - stored as fields to allow ref passing
    private Guid _iidIDXGIResource = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    private Guid _iidIDXGIKeyedMutex = new Guid("9d8e1289-d7b3-465f-8126-250e349af85d");
    
    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateTexture2D(
        IntPtr device,
        ref Texture2DDesc pDesc,
        IntPtr pInitialData,
        out IntPtr ppTexture2D);
    
    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int QueryInterface(
        IntPtr pUnknown,
        ref Guid riid,
        out IntPtr ppvObject);
    
    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint Release(IntPtr pObj);
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public SampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct SampleDesc
    {
        public uint Count;
        public uint Quality;
    }
    
    public SharedTextureHelper(IntPtr device, IntPtr context)
    {
        _device = device;
        _context = context;
    }
    
    /// <summary>
    /// Creates a shared texture with keyed mutex for cross-process rendering.
    /// </summary>
    public bool Create(int width, int height, bool hdr = false)
    {
        if (_disposed) return false;
        
        try
        {
            Destroy();
            
            _width = width;
            _height = height;
            
            uint format = hdr ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
            
            var desc = new Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = 0, // D3D11_USAGE_DEFAULT
                BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = 0,
                MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX,
            };
            
            int hr = CreateTexture2D(_device, ref desc, IntPtr.Zero, out _sharedTexture);
            if (hr < 0)
            {
                Debug.WriteLine($"[SharedTex] Failed to create texture: 0x{hr:X8}");
                return false;
            }
            
            // Get the shared handle via IDXGIResource
            hr = QueryInterface(_sharedTexture, ref _iidIDXGIResource, out IntPtr resource);
            if (hr < 0)
            {
                Debug.WriteLine($"[SharedTex] Failed to get IDXGIResource: 0x{hr:X8}");
                Release(_sharedTexture);
                _sharedTexture = IntPtr.Zero;
                return false;
            }
            
            // Call GetSharedHandle using vtable
            _sharedHandle = GetSharedHandleViaVTable(resource);
            Release(resource);
            
            if (_sharedHandle == IntPtr.Zero)
            {
                Debug.WriteLine($"[SharedTex] Failed to get shared handle");
                Release(_sharedTexture);
                _sharedTexture = IntPtr.Zero;
                return false;
            }
            
            _mutexKey = 0;
            Debug.WriteLine($"[SharedTex] Created: {width}x{height} | Handle: 0x{_sharedHandle:X}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SharedTex] Create exception: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Acquires the keyed mutex before rendering to the shared texture.
    /// </summary>
    public unsafe bool AcquireMutex(uint timeoutMs = 100)
    {
        if (_disposed || _sharedTexture == IntPtr.Zero) return false;
        
        try
        {
            int hr = QueryInterface(_sharedTexture, ref _iidIDXGIKeyedMutex, out IntPtr mutex);
            if (hr < 0) return false;
            
            // IDXGIKeyedMutex::AcquireSync using vtable
            IntPtr vtable = Marshal.ReadIntPtr(mutex);
            IntPtr acquireSyncPtr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size);
            var acquireSync = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)acquireSyncPtr;
            hr = acquireSync(mutex, 0, timeoutMs);
            Release(mutex);
            
            return hr == 0; // S_OK
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Releases the keyed mutex after rendering to the shared texture.
    /// </summary>
    public unsafe bool ReleaseMutex()
    {
        if (_disposed || _sharedTexture == IntPtr.Zero) return false;
        
        try
        {
            int hr = QueryInterface(_sharedTexture, ref _iidIDXGIKeyedMutex, out IntPtr mutex);
            if (hr < 0) return false;
            
            // IDXGIKeyedMutex::ReleaseSync using vtable
            IntPtr vtable = Marshal.ReadIntPtr(mutex);
            IntPtr releaseSyncPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var releaseSync = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)releaseSyncPtr;
            hr = releaseSync(mutex, 0);
            Release(mutex);
            
            if (hr == 0)
            {
                return true;
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Gets the shared handle via IDXGIResource vtable call.
    /// </summary>
    private unsafe IntPtr GetSharedHandleViaVTable(IntPtr resource)
    {
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(resource);
            IntPtr getSharedHandlePtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size); // 4th method (0-indexed)
            var getSharedHandle = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)getSharedHandlePtr;
            int hr = getSharedHandle(resource, out IntPtr handle);
            return hr >= 0 ? handle : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
    
    /// <summary>
    /// Gets the current mutex key for synchronization.
    /// </summary>
    public ulong GetCurrentKey() => _mutexKey;
    
    /// <summary>
    /// Copies the shared texture to a destination texture (e.g., swapchain backbuffer).
    /// </summary>
    public unsafe void CopyTo(IntPtr destination)
    {
        if (_disposed || _sharedTexture == IntPtr.Zero || destination == IntPtr.Zero) return;
        
        try
        {
            // ID3D11DeviceContext::CopyResource is the 47th method in the vtable
            IntPtr vtable = Marshal.ReadIntPtr(_context);
            IntPtr copyResourcePtr = Marshal.ReadIntPtr(vtable, 46 * IntPtr.Size);
            var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)copyResourcePtr;
            copyResource(_context, destination, _sharedTexture);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SharedTex] CopyTo exception: {ex.Message}");
        }
    }
    
    public unsafe void Destroy()
    {
        if (_sharedTexture != IntPtr.Zero)
        {
            // Release mutex if held
            try
            {
                int hr = QueryInterface(_sharedTexture, ref _iidIDXGIKeyedMutex, out IntPtr mutex);
                if (hr >= 0)
                {
                    IntPtr vtable = Marshal.ReadIntPtr(mutex);
                    IntPtr releaseSyncPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                    var releaseSync = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)releaseSyncPtr;
                    releaseSync(mutex, _mutexKey);
                    Release(mutex);
                }
            }
            catch { }
            
            Release(_sharedTexture);
            _sharedTexture = IntPtr.Zero;
        }
        
        _sharedHandle = IntPtr.Zero;
        _mutexKey = 0;
        _width = 0;
        _height = 0;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Destroy();
    }
}
