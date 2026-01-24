using System.Runtime.InteropServices;

namespace Mpv.Core.Structs.Render;

/// <summary>
/// Parameters for initializing the DXGI renderer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MpvDxgiInitParams
{
    /// <summary>
    /// ID3D11Device*
    /// </summary>
    public IntPtr Device;

    /// <summary>
    /// ID3D11DeviceContext*
    /// </summary>
    public IntPtr Context;
}
