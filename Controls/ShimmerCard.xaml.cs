using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class ShimmerCard : UserControl
    {
        public ShimmerCard()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => ShimmerStoryboard.Begin();
        }
    }
}
