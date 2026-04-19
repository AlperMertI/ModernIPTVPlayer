using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

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
                    // Handle Data URIs (Base64)
                    if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                    {
                        int commaIndex = url.IndexOf(",");
                        if (commaIndex > 0)
                        {
                            string base64Data = url.Substring(commaIndex + 1);
                            byte[] bytes = System.Convert.FromBase64String(base64Data);
                            
                            var stream = new InMemoryRandomAccessStream();
                            var writer = stream.AsStreamForWrite();
                            writer.Write(bytes, 0, bytes.Length);
                            writer.Flush();
                            
                            stream.Seek(0);

                            var bmp = new BitmapImage();
                            bmp.SetSource(stream);
                            return bmp;
                        }
                    }

                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        var bmp = new BitmapImage();
                        
                        // Small decoding width for episode thumbs/covers to save RAM and time
                        bmp.DecodePixelWidth = 400; 
                        
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
