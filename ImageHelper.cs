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

namespace ModernIPTVPlayer
{
    public static class ImageHelper
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly ConcurrentDictionary<string, (Color Primary, Color Secondary)> _colorCache = new();
        private static readonly Random _random = new Random();

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
            try
            {
                // Use HttpHelper.Client for better headers and reliability
                using var response = await HttpHelper.Client.GetAsync(imageUrl);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var memStream = new InMemoryRandomAccessStream();
                await stream.CopyToAsync(memStream.AsStreamForWrite());
                memStream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(memStream);

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"COLOR EXTRACTION FAILED: {ex.Message}");
                return (Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 30, 30, 30));
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
    }
}
