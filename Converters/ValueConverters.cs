using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Microsoft.UI;

namespace ModernIPTVPlayer.Converters
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class BoolToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class BoolToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // True = White (Active), False = Gray (Inactive)
            return (value is bool b && b) ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    [Microsoft.UI.Xaml.Data.Bindable]
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

    [Microsoft.UI.Xaml.Data.Bindable]
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is bool b) ? !b : true;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StringToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is string s && !string.IsNullOrEmpty(s)) ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class ActiveBorderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b && b)
            {
                if (Application.Current.Resources.TryGetValue("GoldGradient", out var brush)) return brush;
                return new SolidColorBrush(Colors.Gold);
            }
            return new SolidColorBrush(Windows.UI.Color.FromArgb(32, 255, 255, 255)); // #20FFFFFF
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class ActiveBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b && b)
            {
                if (Application.Current.Resources.TryGetValue("GoldGradient", out var brush))
                {
                    // If it's a brush, we can't easily set opacity if it's a theme resource, 
                    // but we can return it and the Border will handle it.
                    return brush;
                }
                return new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 215, 0)); // Semi-transparent Gold
            }
            return new SolidColorBrush(Colors.Transparent);
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}
