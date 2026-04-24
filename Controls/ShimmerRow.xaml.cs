using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class ShimmerRow : UserControl
    {
        public ShimmerRow()
        {
            this.InitializeComponent();
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            PulseAnimation.Begin();
        }
    }
}
