using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace ModernIPTVPlayer
{
    public class StringToImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    // Suppress invalid URI crashes
                    if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
                    {
                        // Use BitmapImage with automatic decoding
                        return new BitmapImage(uri);
                    }
                }
                catch 
                {
                    // Fallback or ignore
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
