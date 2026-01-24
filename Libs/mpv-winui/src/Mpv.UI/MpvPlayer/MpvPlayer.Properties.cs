using Microsoft.UI.Xaml.Controls;
using Mpv.Core;
using MpvWinUI.Common;

namespace MpvWinUI;

public sealed partial class MpvPlayer
{
    private D3D11RenderControl _renderControl;



    public Player Player { get; private set; }
}
