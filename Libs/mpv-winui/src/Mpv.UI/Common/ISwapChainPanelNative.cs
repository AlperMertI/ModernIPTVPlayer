using Silk.NET.Core.Native;
using System;
using System.Runtime.InteropServices;

namespace MpvWinUI.Common;

public static class NSwapChainPanelNative
{
    // WinUI 3 / Windows App SDK (microsoft.ui.xaml.media.dxinterop.h)
    // OFFICIAL GUID for Microsoft.UI.Xaml.Controls.SwapChainPanel
    public static readonly Guid IID_ISwapChainPanelNative = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");
    
    // UWP / Legacy (windows.ui.xaml.media.dxinterop.h)
    // OFFICIAL GUID for Windows.UI.Xaml.Controls.SwapChainPanel
    public static readonly Guid IID_ISwapChainPanelNative_UWP = new("F92F2C83-05EF-4AA9-A391-76A4952044F4");

    // Standard VTable Slot (0-2 are IUnknown)
    public const int Slot_SetSwapChain = 3;
}
