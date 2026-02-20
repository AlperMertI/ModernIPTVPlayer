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
                    if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
                    {
                        var bmp = new BitmapImage();
                        
                        // Small decoding width for episode thumbs/covers to save RAM and time
                        // Assuming most UI elements binding this are < 400px wide
                        bmp.DecodePixelWidth = 400; 
                        
                        // Decode on a background thread instead of UI thread to prevent hitching
                        bmp.UriSource = uri;
                        return bmp;
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
