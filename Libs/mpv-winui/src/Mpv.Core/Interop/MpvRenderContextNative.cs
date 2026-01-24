// Copyright (c) Bili Copilot. All rights reserved.

using Mpv.Core.Enums.Client;
using Mpv.Core.Enums.Render;
using Mpv.Core.Structs.Client;
using Mpv.Core.Structs.Render;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Mpv.Core.Interop;

public partial class MpvRenderContextNative
{
    public const string MpvRenderApiTypeOpenGL = "opengl";
    public const string MpvRenderApiTypeDXGI = "dxgi";
    public const string MpvRenderApiTypeSoftware = "sw";

    [LibraryImport(MpvIdentifier)]
    private static partial IntPtr mpv_error_string(int error);

    private static string GetMpvErrorString(MpvError error)
    {
        try {
            var ptr = mpv_error_string((int)error);
            return Marshal.PtrToStringAnsi(ptr) ?? "Unknown";
        } catch { return "Error calling mpv_error_string"; }
    }
    public MpvRenderContextNative(MpvHandle coreHandle, MpvRenderParam[] param)
    {
        int paramSize = Marshal.SizeOf<MpvRenderParam>();
        try {
            // Marshalling - ALWAYS add a null terminator (type=0) for mpv
            var ptr = Marshal.AllocHGlobal(paramSize * (param.Length + 1));
            for (var i = 0; i < param.Length; i++)
            {
                Marshal.StructureToPtr(param[i], ptr + (paramSize * i), false);
            }
            // Null terminator
            var terminator = new MpvRenderParam { Type = 0, Data = IntPtr.Zero };
            Marshal.StructureToPtr(terminator, ptr + (paramSize * param.Length), false);

            Debug.WriteLine($"[LOG] Final Attempt: Calling mpv_render_context_create. Params count: {param.Length}");
            
            // Handle'ı ham IntPtr olarak gönderiyoruz (coreHandle.Handle)
            var errorCode = mpv_render_context_create(out var context, coreHandle.Handle, ptr);
            
            Debug.WriteLine($"[LOG] Result: {errorCode} ({GetMpvErrorString(errorCode)})");
            
            Marshal.FreeHGlobal(ptr);

            if (errorCode != MpvError.Success)
            {
                throw new Exception($"Failed: {errorCode} ({GetMpvErrorString(errorCode)})", Utils.CreateError(errorCode));
            }

            Handle = context;
        } catch (Exception ex) {
            Debug.WriteLine($"[FATAL_LOG] CRASH in RenderContext Create: {ex}");
            throw;
        }
    }

    public void SetParameter(MpvRenderParam param)
    {
        var errorCode = mpv_render_context_set_parameter(Handle, param);
        if (errorCode != MpvError.Success)
        {
            throw new Exception($"Failed to set a render context parameter. Error: {errorCode}", CreateError(errorCode));
        }
    }

    public MpvRenderParam GetInformation(MpvRenderParam param)
    {
        var errorCode = mpv_render_context_get_info(Handle, param);
        if (errorCode != MpvError.Success)
        {
            throw new Exception($"Failed to get a render context info. Error: {errorCode}", CreateError(errorCode));
        }

        return param;
    }

    public MpvRenderUpdateFlag Update()
        => mpv_render_context_update(Handle);

    public void SetUpdateCallback(MpvRenderUpdateCallback callback, IntPtr callbackContext)
    {
        var errorCode = mpv_render_context_set_update_callback(Handle, callback, callbackContext);
        if (errorCode != MpvError.Success)
        {
            throw new Exception($"Failed to set a render context update callback. Error: {errorCode}", CreateError(errorCode));
        }
    }

    public void Render(MpvRenderParam[] param)
    {
        if (Handle.Handle == IntPtr.Zero) return;
        
        try {
            var size = Marshal.SizeOf<MpvRenderParam>();
            // ALWAYS add a null terminator (type=0) for mpv
            var ptr = Marshal.AllocHGlobal(size * (param.Length + 1));
            for (var i = 0; i < param.Length; i++)
            {
                Marshal.StructureToPtr(param[i], ptr + (size * i), false);
            }
            // Null terminator
            var terminator = new MpvRenderParam { Type = 0, Data = IntPtr.Zero };
            Marshal.StructureToPtr(terminator, ptr + (size * param.Length), false);

            var errorCode = mpv_render_context_render(Handle, ptr);
            
            Marshal.FreeHGlobal(ptr);

            if (errorCode != MpvError.Success)
            {
                Debug.WriteLine($"[LOG] Render error: {errorCode} ({GetMpvErrorString(errorCode)})");
                throw new Exception($"Failed to render a frame. Error: {errorCode}", Utils.CreateError(errorCode));
            }
        } catch (AccessViolationException ex) {
            Debug.WriteLine($"[CRITICAL_LOG] AccessViolation during Render! ContextHandle: {Handle.Handle}");
            throw;
        } catch (Exception ex) {
            Debug.WriteLine($"[LOG] Render general exception: {ex}");
            throw;
        }
    }

    public void Destroy()
    {
        if (Handle.Handle == IntPtr.Zero) return;
        try
        {
            mpv_render_context_free(Handle.Handle);
        }
        catch (Exception)
        {
        }
    }

    public void ReportSwap()
    {
        var errorCode = mpv_render_context_report_swap(Handle);
        if (errorCode != MpvError.Success)
        {
            throw new Exception($"Failed to report a render context swap. Error: {errorCode}", CreateError(errorCode));
        }
    }

    public MpvRenderContextHandle Handle { get; }
}
