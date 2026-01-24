using System.Runtime.InteropServices;

namespace Mpv.Core.Structs.Render;

/// <summary>
/// Describes a DXGI render target.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MpvDxgiFbo
{
    /// <summary>
    /// ID3D11Texture2D*
    /// </summary>
    public IntPtr Texture;

    /// <summary>
    /// Internal texture width.
    /// </summary>
    public int Width;

    /// <summary>
    /// Internal texture height.
    /// </summary>
    public int Height;
}
