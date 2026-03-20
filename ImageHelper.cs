using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ModernIPTVPlayer
{
    public static class ImageHelper
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly ConcurrentDictionary<string, (Color Primary, Color Secondary)> _colorCache = new();
        private static readonly ConcurrentDictionary<string, BitmapImage> _logoCache = new();
        private static readonly Random _random = new Random();
        private static readonly System.Threading.SemaphoreSlim _decodeSemaphore = new System.Threading.SemaphoreSlim(2);

        private static readonly int MAX_LOGO_CACHE_SIZE = 120;

        /// <summary>
        /// Gets a cached BitmapImage for a logo URL. If not cached, creates one and adds it.
        /// MUST BE CALLED FROM UI THREAD.
        /// </summary>
        public static BitmapImage GetCachedLogo(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_logoCache.TryGetValue(url, out var existing)) return existing;

            // Cache size management
            if (_logoCache.Count > MAX_LOGO_CACHE_SIZE)
            {
                // Partial clear strategy: remove ~half to maintain some hits
                var keysToRemove = _logoCache.Keys.Take(MAX_LOGO_CACHE_SIZE / 2).ToList();
                foreach (var key in keysToRemove) _logoCache.TryRemove(key, out _);
            }

            try
            {
                var bitmap = new BitmapImage();
                // Optimization: Logos are never shown very large, usually < 120px height
                bitmap.DecodePixelHeight = 120;
                bitmap.CreateOptions = BitmapCreateOptions.None; // WinUI 3 handles decoding asynchronously by default
                bitmap.UriSource = new Uri(url);
                
                _logoCache.TryAdd(url, bitmap);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<(Color Primary, Color Secondary)?> GetOrExtractColorAsync(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl)) return null;
            if (_colorCache.TryGetValue(imageUrl, out var cached)) return cached;

            try
            {
                // FALLBACK: If we must use URL, we now use HttpHelper for better headers
                var colors = await ExtractDominantColorsAsync(imageUrl);
                _colorCache.TryAdd(imageUrl, colors);
                return colors;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"COLOR ERROR: {ex.Message}");
                return null;
            }
        }

        public static (Color Primary, Color Secondary) ExtractColorsFromPixels(byte[] pixels, int width, int height, string cacheKey = null)
        {
            try
            {
                var (color1, _, color2, _) = FindTopTwoColorsWithRatios(pixels, width, height);
                
                // Randomly decide which side gets the dominant color
                bool dominantOnLeft = _random.Next(2) == 0;
                var result = dominantOnLeft ? (color1, color2) : (color2, color1);

                if (!string.IsNullOrEmpty(cacheKey))
                {
                    _colorCache.TryAdd(cacheKey, result);
                }

                return result;
            }
            catch
            {
                var fallback = Color.FromArgb(255, 30, 30, 30);
                return (fallback, fallback);
            }
        }

        private static async Task<(Color Primary, Color Secondary)> ExtractDominantColorsAsync(string imageUrl)
        {
            await _decodeSemaphore.WaitAsync();
            try
            {
                using var response = await HttpHelper.Client.GetAsync(imageUrl);
                response.EnsureSuccessStatusCode();

                using (var stream = await RandomAccessStreamReference.CreateFromUri(new Uri(imageUrl)).OpenReadAsync())
                {
                    if (stream == null || stream.Size == 0) return (Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 30, 30, 30));
                    
                    var decoder = await BitmapDecoder.CreateAsync(stream);

                    uint targetSize = 50;
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = targetSize,
                        ScaledHeight = targetSize,
                        InterpolationMode = BitmapInterpolationMode.Linear
                    };

                    var pixelData = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    var pixels = pixelData.DetachPixelData();
                    int width = (int)targetSize;
                    int height = (int)targetSize;

                    var (color1, ratio1, color2, ratio2) = FindTopTwoColorsWithRatios(pixels, width, height);

                    // Randomly decide which side gets the dominant color
                    bool dominantOnLeft = _random.Next(2) == 0;

                    if (dominantOnLeft)
                        return (color1, color2);
                    else
                        return (color2, color1);
                }
            }
            catch (Exception ex)
            {
                string hResult = string.Format("0x{0:X}", ex.HResult);
                System.Diagnostics.Debug.WriteLine($"[ImageHelper] !!! COLOR EXTRACTION FAILED for {imageUrl}");
                System.Diagnostics.Debug.WriteLine($"[ImageHelper] Type: {ex.GetType().Name}, HResult: {hResult}");
                System.Diagnostics.Debug.WriteLine($"[ImageHelper] Message: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ImageHelper] StackTrace: {ex.StackTrace}");
                
                return (Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 30, 30, 30));
            }
            finally
            {
                _decodeSemaphore.Release();
            }
        }

        private static (Color color1, float ratio1, Color color2, float ratio2) FindTopTwoColorsWithRatios(byte[] pixels, int width, int height)
        {
            Dictionary<int, (long R, long G, long B, int Count)> buckets = new();
            int totalValid = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];

                    // Skip only pure black
                    if (r < 15 && g < 15 && b < 15) continue;

                    totalValid++;

                    // Quantize (3 bits)
                    int hash = (r >> 5) << 6 | (g >> 5) << 3 | (b >> 5);

                    if (!buckets.ContainsKey(hash)) buckets[hash] = (0, 0, 0, 0);
                    var entry = buckets[hash];
                    buckets[hash] = (entry.R + r, entry.G + g, entry.B + b, entry.Count + 1);
                }
            }

            if (buckets.Count == 0 || totalValid == 0)
            {
                var grey = Color.FromArgb(255, 100, 100, 100);
                return (grey, 0.5f, grey, 0.5f);
            }

            var topBuckets = buckets.Values.OrderByDescending(v => v.Count).Take(10).ToList();
            var colors = topBuckets.Select(bucket => (
                Color: Color.FromArgb(255, (byte)(bucket.R / bucket.Count), (byte)(bucket.G / bucket.Count), (byte)(bucket.B / bucket.Count)),
                Count: bucket.Count,
                Percent: (float)bucket.Count / totalValid
            )).ToList();

            var neutrals = colors.Where(c => !IsVibrantColor(c.Color)).OrderByDescending(c => c.Count).ToList();
            var vibrants = colors.Where(c => IsVibrantColor(c.Color)).OrderByDescending(c => c.Count).ToList();

            Color bestC1, bestC2;
            float pct1, pct2;

            if (vibrants.Count >= 2)
            {
                bestC1 = vibrants[0].Color;
                bestC2 = vibrants[1].Color;
                pct1 = vibrants[0].Percent;
                pct2 = vibrants[1].Percent;
            }
            else if (vibrants.Count == 1 && neutrals.Count >= 1)
            {
                bestC1 = neutrals[0].Color;
                bestC2 = vibrants[0].Color;
                pct1 = neutrals[0].Percent;
                pct2 = vibrants[0].Percent;
            }
            else
            {
                bestC1 = colors[0].Color;
                bestC2 = colors.Count > 1 ? colors[1].Color : colors[0].Color;
                pct1 = colors[0].Percent;
                pct2 = colors.Count > 1 ? colors[1].Percent : pct1;
            }

            return (BoostSaturation(bestC1, 1.4f), pct1, BoostSaturation(bestC2, 1.4f), pct2);
        }

        private static bool IsVibrantColor(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            return (max - min) > 35;
        }

        private static Color BoostSaturation(Color color, float factor)
        {
            float r = color.R;
            float g = color.G;
            float b = color.B;
            float gray = (r + g + b) / 3.0f;
            r = gray + (r - gray) * factor;
            g = gray + (g - gray) * factor;
            b = gray + (b - gray) * factor;
            return Color.FromArgb(255, (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
        }

        public static Color LightenColor(Color color, float factor)
        {
            return Color.FromArgb(
                color.A,
                (byte)Math.Min(255, color.R * factor),
                (byte)Math.Min(255, color.G * factor),
                (byte)Math.Min(255, color.B * factor)
            );
        }

        /// <summary>
        /// APCA (Advanced Perceptual Contrast Algorithm) implementation for modern readability.
        /// Returns Lc (Contrast Value) between -106 and 106.
        /// </summary>
        public static double GetContrastAPCA(Color textColor, Color backgroundColor)
        {
            double txtY = GetPerceptualLuminance(textColor);
            double bgY = GetPerceptualLuminance(backgroundColor);

            double contrast = 0;
            double sapcScale = 1.14;

            if (bgY > txtY)
            {
                // Light background, dark text
                contrast = (Math.Pow(bgY, 0.56) - Math.Pow(txtY, 0.57)) * sapcScale;
                contrast = (contrast < 0.022) ? 0 : (contrast - 0.027) * 40.0;
            }
            else
            {
                // Dark background, light text
                contrast = (Math.Pow(bgY, 0.62) - Math.Pow(txtY, 0.65)) * sapcScale;
                contrast = (contrast > -0.022) ? 0 : (contrast + 0.027) * 40.0;
            }

            // APCA result is already scaled by 40.0 to be in the 0-100 range.
            // Redundant * 100.0 was causing values to always hit the clamp.
            return Math.Clamp(contrast, -108, 108);
        }

        private static double GetPerceptualLuminance(Color c)
        {
            // Simple sRGB to Luminance with APCA weights
            double r = Math.Pow(c.R / 255.0, 2.4);
            double g = Math.Pow(c.G / 255.0, 2.4);
            double b = Math.Pow(c.B / 255.0, 2.4);
            
            // APCA 0.98G weights
            return (r * 0.2126729) + (g * 0.7151522) + (b * 0.0721750);
        }

        public static Color GetContrastSafeColor(Color baseColor, Color backgroundColor, double targetLc = 75)
        {
            // Evaluate both directions to find the best inherent contrast
            double whiteLc = Math.Abs(GetContrastAPCA(Color.FromArgb(255, 255, 255, 255), backgroundColor));
            double blackLc = Math.Abs(GetContrastAPCA(Color.FromArgb(255, 0, 0, 0), backgroundColor));

            bool currentIsReadable = Math.Abs(GetContrastAPCA(baseColor, backgroundColor)) >= targetLc;
            if (currentIsReadable) return baseColor;

            // Pick direction based on what can potentially reach higher contrast.
            // Bias heavily towards "lighten" (White text) because our UI system can 
            // protect white text with darkening gradients, but can't easily protect dark text.
            bool lighten = whiteLc >= (blackLc - 25);

            Color current = baseColor;
            for (int i = 0; i < 12; i++)
            {
                if (lighten)
                {
                    // Lighten text
                    current = Color.FromArgb(255, 
                        (byte)Math.Min(255, current.R + 25), 
                        (byte)Math.Min(255, current.G + 25), 
                        (byte)Math.Min(255, current.B + 25));
                }
                else
                {
                    // Darken text
                    current = Color.FromArgb(255, 
                        (byte)Math.Max(0, current.R - 25), 
                        (byte)Math.Max(0, current.G - 25), 
                        (byte)Math.Max(0, current.B - 25));
                }

                if (Math.Abs(GetContrastAPCA(current, backgroundColor)) >= targetLc)
                    return current;
            }

            return lighten ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);
        }

        public static Color ExtractAreaAverageColor(byte[] pixels, int width, int height, double areaLeft, double areaTop, double areaWidth, double areaHeight)
        {
            try
            {
                long sumR = 0, sumG = 0, sumB = 0, count = 0;

                int xStart = (int)(areaLeft * width);
                int yStart = (int)(areaTop * height);
                int xEnd = (int)((areaLeft + areaWidth) * width);
                int yEnd = (int)((areaTop + areaHeight) * height);

                xStart = Math.Clamp(xStart, 0, width - 1);
                yStart = Math.Clamp(yStart, 0, height - 1);
                xEnd = Math.Clamp(xEnd, 0, width);
                yEnd = Math.Clamp(yEnd, 0, height);

                for (int y = yStart; y < yEnd; y++)
                {
                    for (int x = xStart; x < xEnd; x++)
                    {
                        int i = (y * width + x) * 4;
                        if (i + 3 >= pixels.Length) continue;

                        sumB += pixels[i];
                        sumG += pixels[i + 1];
                        sumR += pixels[i + 2];
                        count++;
                    }
                }

                if (count == 0) return Color.FromArgb(255, 20, 20, 20);

                return Color.FromArgb(255, (byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
            }
            catch
            {
                return Color.FromArgb(255, 20, 20, 20);
            }
        }

        public static bool IsPlaceholder(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;

            // Known generic placeholders
            if (url.Contains("stremio.torbox.app/background/default", StringComparison.OrdinalIgnoreCase)) return true;
            if (url.Contains("images.metahub.space/background/small/", StringComparison.OrdinalIgnoreCase) && url.EndsWith("/img")) return true;
            
            // Generic keywords in URL that usually indicate no content
            string lowerUrl = url.ToLowerInvariant();
            if (lowerUrl.Contains("placeholder") || 
                lowerUrl.Contains("no-image") || 
                lowerUrl.Contains("default_backdrop") || 
                lowerUrl.Contains("no_poster") ||
                lowerUrl.Contains("image-not-found") ||
                lowerUrl.Contains("blank-profile")) 
            {
                return true;
            }

            return false;
        }
    }
}
