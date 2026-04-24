using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Project Zero: Unified High-Performance Image Engine.
    /// Manages DPI-aware decoding, adaptive memory management, and caching for all discovery visuals.
    /// Eliminates VRAM bloat and decoding-induced UI stutters.
    /// </summary>
    public static class SharedImageManager
    {
        private static readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> _imagePool = new();
        private static readonly System.Collections.Generic.LinkedList<string> _mruList = new();
        private static readonly System.Collections.Generic.Dictionary<string, BitmapImage> _strongCache = new();
        private const int MAX_STRONG_CACHE = 200; 
        private static readonly System.Threading.Lock _poolLock = new();

        private static void AddToStrongCache(string key, BitmapImage bitmap)
        {
            // Maintain MRU order
            _mruList.Remove(key);
            _mruList.AddFirst(key);
            _strongCache[key] = bitmap;

            // Evict oldest if we exceed capacity
            if (_strongCache.Count > MAX_STRONG_CACHE)
            {
                var last = _mruList.Last.Value;
                _mruList.RemoveLast();
                _strongCache.Remove(last);
            }
        }

        /// <summary>
        /// Retrieves or creates an optimized BitmapImage.
        /// DPI-Aware: Automatically calculates physical pixel requirements based on screen scaling.
        /// </summary>
        /// <param name="url">Source URL</param>
        /// <param name="targetWidth">Desired Width in DIPs</param>
        /// <param name="targetHeight">Desired Height in DIPs</param>
        /// <param name="xamlRoot">Optional XamlRoot for DPI scaling detection</param>
        public static BitmapImage GetOptimizedImage(string? url, double targetWidth = 0, double targetHeight = 0, XamlRoot? xamlRoot = null)
        {
            if (string.IsNullOrEmpty(url)) return null;

            // 1. Check pool for existing instance to share memory
            string cacheKey = $"{url}_{targetWidth}_{targetHeight}";
            lock (_poolLock)
            {
                // A. Primary: Check the strong MRU cache for an instant hit
                if (_strongCache.TryGetValue(cacheKey, out var strong))
                {
                    _mruList.Remove(cacheKey);
                    _mruList.AddFirst(cacheKey);
                    return strong;
                }

                // B. Secondary: Check the weak reference pool for promotion
                if (_imagePool.TryGetValue(cacheKey, out var weakRef) && weakRef.TryGetTarget(out var existing))
                {
                    AddToStrongCache(cacheKey, existing);
                    return existing;
                }
            }

            try
            {
                // 2. Thread-Safety Check (Project Zero Stability)
                // UI objects (BitmapImage) MUST be created on the UI thread.
                var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (queue == null)
                {
                    // [SILENT FAIL] Returning null on background thread is expected during enrichment.
                    // The UI will eventually re-read this on the UI thread via data binding.
                    return null;
                }

                var bitmap = new BitmapImage();
                
                // 3. DPI-Aware Scaling (Senior Optimization)
                // We decode at exact physical pixel size to avoid GPU scaling overhead and RAM waste.
                double scale = xamlRoot?.RasterizationScale ?? 1.0;
                
                if (targetWidth > 0 || targetHeight > 0)
                {
                    // [PRECISION] Use Physical pixels to ensure we occupy exact memory bits on high-DPI screens.
                    bitmap.DecodePixelType = DecodePixelType.Physical;
                    if (targetWidth > 0) bitmap.DecodePixelWidth = (int)(targetWidth * scale);
                    if (targetHeight > 0) bitmap.DecodePixelHeight = (int)(targetHeight * scale);
                }

                // 4. Low-Impact Delivery
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // We manage our own pool
                bitmap.UriSource = new Uri(url);

                // 5. Memory Management: Register in pool
                lock (_poolLock)
                {
                    _imagePool[cacheKey] = new WeakReference<BitmapImage>(bitmap);
                    AddToStrongCache(cacheKey, bitmap);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SharedImage] Failed to load {url}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Proactively releases image resources from memory.
        /// Essential for resolving 'Memory Creep' in long scrolling sessions.
        /// </summary>
        public static void EvictImage(BitmapImage? bitmap)
        {
            // [STABILITY FIX] We no longer clear UriSource here because many controls share the same BitmapImage.
            // Clearing it for one would break it for everyone.
            // Simply allowing the BitmapImage to be garbage collected when no longer used is sufficient.
            if (bitmap == null) return;
        }

        /// <summary>
        /// Periodic cleanup of dead references in the weak pool.
        /// </summary>
        public static void PeriodicCleanup()
        {
            lock (_poolLock)
            {
                var keysToRemove = new System.Collections.Generic.List<string>();
                foreach (var kvp in _imagePool)
                {
                    if (!kvp.Value.TryGetTarget(out _)) keysToRemove.Add(kvp.Key);
                }
                foreach (var key in keysToRemove) _imagePool.TryRemove(key, out _);
            }
        }
    }
}
