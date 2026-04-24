using Microsoft.UI.Xaml.Controls;
using ModernIPTVPlayer.Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace ModernIPTVPlayer.Models.Stremio
{
    /// <summary>
    /// PROJECT ZERO: High-performance recycling collection for Stremio Discovery.
    /// Manages a pool of 'Slim Proxies' to keep allocations near zero regardless of catalog size.
    /// Supports IKeyIndexMapping for 'ItemsRepeater' optimization.
    /// </summary>
    public sealed partial class StremioVirtualCollection : ReadOnlyVirtualListBase<StremioMediaStream>, IKeyIndexMapping, System.Collections.Specialized.INotifyCollectionChanged
    {
        private readonly List<StremioMeta> _data;
        private string? _sourceAddon;
        private readonly ConcurrentDictionary<int, StremioMediaStream> _activeProxies = new();
        private readonly ConcurrentQueue<StremioMediaStream> _proxyPool = new();
        private readonly System.Threading.Lock _syncLock = new();

        public event System.Collections.Specialized.NotifyCollectionChangedEventHandler CollectionChanged;

        public StremioVirtualCollection(IEnumerable<StremioMeta> items, string? sourceAddon = null)
        {
            _data = items.ToList();
            _sourceAddon = sourceAddon;
        }

        public override int Count => _data.Count;

        /// <summary>
        /// PROJECT ZERO: Grows the collection with new data.
        /// Essential for 'Load More' in infinite scroll catalogs.
        /// </summary>
        public void AddRange(IEnumerable<StremioMeta> items)
        {
            if (items == null || !items.Any()) return;

            int startingIndex;
            var newList = items.ToList();

            lock (_syncLock)
            {
                startingIndex = _data.Count;
                _data.AddRange(newList);
            }

            CollectionChanged?.Invoke(this, new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// Materializes or recycles a proxy for the given index.
        /// </summary>
        public override StremioMediaStream this[int index]
        {
            get
            {
                if (index < 0 || index >= _data.Count) return null!;

                lock (_syncLock)
                {
                    // 1. If already materialized and active, return it.
                    if (_activeProxies.TryGetValue(index, out var existing))
                    {
                        return existing;
                    }

                    // 2. Try to get from pool or create new
                    if (!_proxyPool.TryDequeue(out var proxy))
                    {
                        proxy = new StremioMediaStream();
                    }

                    // 3. Re-hydrate the proxy with new data (Flyweight switch)
                    proxy.Meta = _data[index];
                    proxy.SourceAddon = _sourceAddon;
                    _activeProxies[index] = proxy;

                    // 4. Cleanup distant proxies to prevent pool bloat
                    EvictDistantProxies(index);

                    return proxy;
                }
            }
        }

        private void EvictDistantProxies(int currentIndex)
        {
            // Keep roughly 100 items around the current viewport materialized.
            // Items outside this range that are NOT PINNED go back to the pool.
            const int bufferSize = 100;
            var keysToEvict = _activeProxies.Keys
                .Where(k => Math.Abs(k - currentIndex) > bufferSize)
                .ToList();

            foreach (var key in keysToEvict)
            {
                if (_activeProxies.TryRemove(key, out var proxy))
                {
                    if (!proxy.IsPinned)
                    {
                        _proxyPool.Enqueue(proxy);
                    }
                    else
                    {
                        // If pinned (e.g. in ExpandedCard), keep it active
                        _activeProxies[key] = proxy;
                    }
                }
            }
        }

        // IKeyIndexMapping Implementation
        // Helps ItemsRepeater stay perfectly synced during fast scrolls.
        public string KeyFromIndex(int index) 
        {
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("StremioVirtualCollection.cs:KeyFromIndex", "enter", new System.Collections.Generic.Dictionary<string, object?> { ["idx"] = index }, "H-VIRT"); } catch { }
            // #endregion
            return (index >= 0 && index < _data.Count) ? (_data[index].Id ?? index.ToString()) : index.ToString();
        }
        public int IndexFromKey(string key)
        {
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("StremioVirtualCollection.cs:IndexFromKey", "enter", new System.Collections.Generic.Dictionary<string, object?> { ["key"] = key }, "H-VIRT"); } catch { }
            // #endregion
            return _data.FindIndex(m => m.Id == key);
        }

        public void Clear()
        {
            lock (_syncLock)
            {
                _activeProxies.Clear();
                _data.Clear();
            }
        }
    }
}
