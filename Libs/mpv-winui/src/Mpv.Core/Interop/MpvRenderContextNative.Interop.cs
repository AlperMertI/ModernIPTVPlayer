// Copyright (c) Bili Copilot. All rights reserved.

using Mpv.Core.Enums.Client;
using Mpv.Core.Enums.Render;
using Mpv.Core.Structs.Client;
using Mpv.Core.Structs.Render;
using System.Runtime.InteropServices;

namespace Mpv.Core.Interop;

public partial class MpvRenderContextNative
{
    [LibraryImport(MpvIdentifier)]
    private static partial MpvError mpv_render_context_create(out IntPtr context, IntPtr handle, IntPtr param);

    [LibraryImport(MpvIdentifier)]
    private static partial MpvError mpv_render_context_set_parameter(IntPtr context, MpvRenderParam param);

    [LibraryImport(MpvIdentifier)]
    private static partial MpvError mpv_render_context_get_info(IntPtr context, MpvRenderParam param);

    [LibraryImport(MpvIdentifier)]
    private static partial MpvError mpv_render_context_set_update_callback(IntPtr context, MpvRenderUpdateCallback callback, IntPtr callbackContext);

    [LibraryImport(MpvIdentifier)]
    private static partial MpvRenderUpdateFlag mpv_render_context_update(IntPtr context);

    [LibraryImport(MpvIdentifier)]
    private static partial MpvError mpv_render_context_render(IntPtr context, IntPtr param);

    [LibraryImport(MpvIdentifier)]
    private static partial void mpv_render_context_report_swap(IntPtr context);

    [LibraryImport(MpvIdentifier)]
    private static partial void mpv_render_context_free(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MpvRenderUpdateCallback(IntPtr callbackCtx);
}
