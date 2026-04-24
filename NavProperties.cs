using Microsoft.UI.Xaml;

namespace ModernIPTVPlayer
{
    public static class NavProperties
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(NavProperties), new PropertyMetadata(string.Empty));

        public static string GetIcon(DependencyObject obj) => (string)obj.GetValue(IconProperty);
        public static void SetIcon(DependencyObject obj, string value) => obj.SetValue(IconProperty, value);
        public static readonly DependencyProperty IsCompactProperty =
            DependencyProperty.RegisterAttached("IsCompact", typeof(bool), typeof(NavProperties), new PropertyMetadata(false));

        public static bool GetIsCompact(DependencyObject obj) => (bool)obj.GetValue(IsCompactProperty);
        public static void SetIsCompact(DependencyObject obj, bool value) => obj.SetValue(IsCompactProperty, value);
    }
}
