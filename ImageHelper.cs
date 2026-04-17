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
        // Phase 3.4: Shared HttpClient (connection pool reuse)
        private static readonly HttpClient _httpClient = HttpHelper.Client;
        private static readonly ConcurrentDictionary<string, (Color Primary, Color Secondary)> _colorCache = new();
        private static readonly ConcurrentDictionary<string, BitmapImage> _logoCache = new();
        private static readonly ConcurrentDictionary<string, BitmapImage> _posterCache = new();
        private static readonly ConcurrentDictionary<string, Task<(Color Primary, Color Secondary)>> _pendingExtractions = new();
        private static readonly System.Threading.SemaphoreSlim _extractionSemaphore = new System.Threading.SemaphoreSlim(4, 4);
        private static readonly Random _random = new Random();

        private static readonly int MAX_LOGO_CACHE_SIZE = 120;

        /// <summary>
        /// Centralized image engine for Project Zero. 
        /// Provides optimized BitmapImage with optional DecodePixel constraints to save Native Memory.
        /// MUST BE CALLED FROM UI THREAD.
        /// </summary>
        public static BitmapImage GetImage(string url, int decodeWidth = 0, int decodeHeight = 0)
        {
            if (string.IsNullOrEmpty(url)) return null;

            // Use poster cache for small/medium images (DecodePixel < 500)
            bool isThumbnail = (decodeWidth > 0 && decodeWidth < 500) || (decodeHeight > 0 && decodeHeight < 500);
            if (isThumbnail && _posterCache.TryGetValue(url, out var existing)) return existing;

            try
            {
                var bitmap = new BitmapImage();
                
                // CRITICAL: Prevent WinUI from holding the full-resolution source in its internal cache
                // if we are providing a decoded constraint.
                if (decodeWidth > 0 || decodeHeight > 0)
                {
                    bitmap.DecodePixelType = DecodePixelType.Logical;
                    if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
                    if (decodeHeight > 0) bitmap.DecodePixelHeight = decodeHeight;
                }

                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // We'll manage hit-testing for big items ourselves
                bitmap.UriSource = new Uri(url);

                if (isThumbnail)
                {
                    // Cache management (LIFO-ish simple clear)
                    if (_posterCache.Count > 200) _posterCache.Clear();
                    _posterCache.TryAdd(url, bitmap);
                }

                return bitmap;
            }
            catch { return null; }
        }

        /// <summary>
        /// Gets a cached BitmapImage for a logo URL. If not cached, creates one and adds it.
        /// MUST BE CALLED FROM UI THREAD.
        /// </summary>
        public static BitmapImage GetCachedLogo(string url)
        {
            return GetImage(url, 0, 120); // Logos are usually height-constrained
        }

        public static async Task<(Color Primary, Color Secondary)?> GetOrExtractColorAsync(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl)) return null;
            if (_colorCache.TryGetValue(imageUrl, out var cached)) return cached;

            // Single-flight: keep exactly one extraction task per URL. The task itself seeds the
            // color cache BEFORE clearing the pending map, so a late caller either sees the task
            // or the finished cache entry — never a brief window where both are empty (which caused
            // duplicate fetches when the original _pendingExtractions.TryRemove ran first).
            var task = _pendingExtractions.GetOrAdd(imageUrl, async (url) =>
            {
                await _extractionSemaphore.WaitAsync();
                try
                {
                    var colors = await ExtractDominantColorsAsync(url);
                    _colorCache.TryAdd(url, colors);
                    return colors;
                }
                finally
                {
                    _extractionSemaphore.Release();
                    _pendingExtractions.TryRemove(url, out _);
                }
            });

            try
            {
                return await task;
            }
            catch
            {
                var fallback = (Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 30, 30, 30));
                _colorCache.TryAdd(imageUrl, fallback);
                return fallback;
            }
        }

        /// <summary>Extract (and cache) dominant colors directly from an in-memory image buffer.
        /// Lets the Hero asset pipeline reuse one HTTP fetch for both GPU surface + color extraction.</summary>
        public static async Task<(Color Primary, Color Secondary)?> GetOrExtractColorFromBytesAsync(string cacheKey, byte[] bytes)
        {
            if (string.IsNullOrEmpty(cacheKey) || bytes == null || bytes.Length == 0) return null;
            if (_colorCache.TryGetValue(cacheKey, out var cached)) return cached;

            try
            {
                using var ms = new InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                ms.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(ms);
                uint targetSize = 128;
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

                var (c1, _, c2, _) = FindTopTwoColorsWithRatios(pixels, (int)targetSize, (int)targetSize);
                bool dominantOnLeft = _random.Next(2) == 0;
                var result = dominantOnLeft ? (c1, c2) : (c2, c1);
                _colorCache.TryAdd(cacheKey, result);
                return result;
            }
            catch
            {
                var fallback = (Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 30, 30, 30));
                _colorCache.TryAdd(cacheKey, fallback);
                return fallback;
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var stream = await RandomAccessStreamReference.CreateFromUri(new Uri(imageUrl)).OpenReadAsync())
                {
                    if (stream == null || stream.Size == 0) return (Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 30, 30, 30));
                    
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    sw.Stop();
                    System.Diagnostics.Debug.WriteLine($"[NET-COLOR] ExtractDominantColorsAsync FETCH/DECODE DONE: {Path.GetFileName(imageUrl)} | Took: {sw.ElapsedMilliseconds}ms");
                    sw.Restart();

                    uint targetSize = 128;
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
                // [SILENCED] Broken links are common in IPTV providers (404/403). 
                // We handle this gracefully without flooding the logs.
                // System.Diagnostics.Debug.WriteLine($"[ImageHelper] Extraction Failed for {imageUrl}: {ex.Message}");
                
                return (Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 30, 30, 30));
            }
            finally
            {
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
