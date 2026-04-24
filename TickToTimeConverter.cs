using System;
using Microsoft.UI.Xaml.Data;

namespace ModernIPTVPlayer
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class TickToTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double seconds)
            {
                var ts = TimeSpan.FromSeconds(seconds);
                return ts.TotalHours >= 1 
                    ? ts.ToString(@"h\:mm\:ss") 
                    : ts.ToString(@"mm\:ss");
            }
            return "00:00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
