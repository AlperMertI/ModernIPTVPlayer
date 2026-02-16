using Microsoft.UI.Xaml;

namespace ModernIPTVPlayer
{
    public static class NavProperties
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(NavProperties), new PropertyMetadata(null));

        public static string GetIcon(DependencyObject obj) => (string)obj.GetValue(IconProperty);
        public static void SetIcon(DependencyObject obj, string value) => obj.SetValue(IconProperty, value);
    }
}
