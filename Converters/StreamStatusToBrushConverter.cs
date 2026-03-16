using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace ModernIPTVPlayer.Converters
{
    public class StreamStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is LiveStreamStatus status)
            {
                return status switch
                {
                    LiveStreamStatus.Online => new SolidColorBrush(Colors.LimeGreen),
                    LiveStreamStatus.Unstable => new SolidColorBrush(Colors.Orange),
                    LiveStreamStatus.Offline => new SolidColorBrush(Colors.Red),
                    _ => new SolidColorBrush(Colors.Gray)
                };
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}
