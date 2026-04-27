using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// PROJECT ZERO: Ultra-Performance Image Engine.
    /// Optimized for 120Hz scrolling with O(1) cache lookups and zero-impact eviction.
    /// </summary>
    public static class SharedImageManager
    {
        // Secondary weak pool for memory sharing
        private static readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> _weakPool = new();
        
        // Primary strong cache for instant hits (O(1) access)
        private static readonly Dictionary<string, BitmapImage> _strongCache = new();
        private static readonly Queue<string> _evictionQueue = new();
        
        private const int MAX_STRONG_CACHE = 250; 
        private static readonly System.Threading.Lock _cacheLock = new();

        public static BitmapImage GetOptimizedImage(string? url, double targetWidth = 0, double targetHeight = 0, XamlRoot? xamlRoot = null)
        {
            if (string.IsNullOrEmpty(url)) return null;

            string cacheKey = $"{url}_{targetWidth}_{targetHeight}";

            lock (_cacheLock)
            {
                // 1. O(1) Strong Hit
                if (_strongCache.TryGetValue(cacheKey, out var strong)) return strong;

                // 2. Weak Promotion
                if (_weakPool.TryGetValue(cacheKey, out var weakRef) && weakRef.TryGetTarget(out var promoted))
                {
                    _strongCache[cacheKey] = promoted;
                    _evictionQueue.Enqueue(cacheKey);
                    return promoted;
                }
            }

            // 3. UI Thread Creation & Throttled Load
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue == null) return null;

            try
            {
                var bitmap = new BitmapImage();
                double scale = xamlRoot?.RasterizationScale ?? 1.0;
                
                if (targetWidth > 0 || targetHeight > 0)
                {
                    bitmap.DecodePixelType = DecodePixelType.Physical;
                    if (targetWidth > 0) bitmap.DecodePixelWidth = (int)(targetWidth * scale);
                    if (targetHeight > 0) bitmap.DecodePixelHeight = (int)(targetHeight * scale);
                }

                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

                lock (_cacheLock)
                {
                    // Cache the shell immediately so concurrent requests for the same URL get the same instance
                    _weakPool[cacheKey] = new WeakReference<BitmapImage>(bitmap);
                    _strongCache[cacheKey] = bitmap;
                    _evictionQueue.Enqueue(cacheKey);

                    // Reduced capacity for "Zero-Trace" - keep only the active working set
                    while (_strongCache.Count > 100 && _evictionQueue.TryDequeue(out var oldKey))
                    {
                        _strongCache.Remove(oldKey);
                    }

                    // PROJECT ZERO: Proactive Weak Pool Pruning (Prevent WeakReference leak)
                    if (_weakPool.Count > 1000)
                    {
                        PeriodicCleanup();
                    }
                }

                // PROJECT ZERO V3: Zero-Trace Async Loading
                // Use a weak reference to the bitmap inside the task.
                // If the UI has already cleared the image (due to scrolling away), 
                // and it was evicted from strong cache, the WeakReference will fail 
                // and the task will abort, saving RAM and CPU.
                var weakBitmap = new WeakReference<BitmapImage>(bitmap);

                queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (weakBitmap.TryGetTarget(out var b))
                    {
                        try { b.UriSource = new Uri(url); } catch { }
                    }
                });

                return bitmap;
            }
            catch { return null; }
        }

        public static void PeriodicCleanup()
        {
            lock (_cacheLock)
            {
                foreach (var key in _weakPool.Keys)
                {
                    if (!_weakPool[key].TryGetTarget(out _))
                        _weakPool.TryRemove(key, out _);
                }
            }
        }
    }
}
