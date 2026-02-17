using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Microsoft.UI;

namespace ModernIPTVPlayer.Converters
{
    public class BoolToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    public class BoolToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // True = White (Active), False = Gray (Inactive)
            return (value is bool b && b) ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    public class BoolToColorInvertConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // True = Gray (Inactive), False = White (Active) -> Used when the OTHER tab is active
            // Actually, for "IsAudioDelayMode", if True (Audio), then Audio icon is White.
            // If True (Audio), then Subtitle icon should be Gray.
            return (value is bool b && b) ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136)) : new SolidColorBrush(Colors.White);
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}
