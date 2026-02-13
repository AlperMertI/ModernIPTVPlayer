using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ModernIPTVPlayer
{
    public class BooleanInverseToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b)
            {
                // Returns Visible if FALSE
                return b ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
