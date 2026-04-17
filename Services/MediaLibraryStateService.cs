using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// ARCHITECTURAL UPDATE: Stateful Collection Registry.
    /// Ensures reference stability for filtered media lists to prevent GridView re-bind flickers.
    /// </summary>
    public class MediaLibraryStateService
    {
        private static MediaLibraryStateService _instance;
        public static MediaLibraryStateService Instance => _instance ??= new MediaLibraryStateService();

        // Key: (MediaType, CategoryId), Value: The exact IReadOnlyList instance
        private readonly ConcurrentDictionary<(MediaType, string), IEnumerable> _collectionRegistry = new();
        private string _currentScopeKey = "default";

        /// <summary>
        /// Retrieves or creates a reference-stable collection for a specific category.
        /// </summary>
        public IEnumerable GetOrCreateCollection(MediaType type, string categoryId, Func<IEnumerable> creator)
        {
            var key = (type, categoryId);
            
            // If we already have this list object, return the exact same instance.
            // GridView will see the same reference and skip the layout destruction.
            if (_collectionRegistry.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var newCollection = creator();
            _collectionRegistry.TryAdd(key, newCollection);
            return newCollection;
        }

        public bool TryGetCollection(MediaType type, string categoryId, out IEnumerable collection)
        {
            var key = (type, categoryId);
            return _collectionRegistry.TryGetValue(key, out collection);
        }

        /// <summary>
        /// Updates the active data scope. Scope changes invalidate stale filtered collections.
        /// </summary>
        public void UpdateScope(string scopeKey)
        {
            string normalized = string.IsNullOrWhiteSpace(scopeKey) ? "default" : scopeKey;
            if (string.Equals(_currentScopeKey, normalized, StringComparison.Ordinal)) return;

            _currentScopeKey = normalized;
            _collectionRegistry.Clear();
        }

        /// <summary>
        /// Clears the registry when the user switches accounts or clears cache.
        /// </summary>
        public void Invalidate()
        {
            _collectionRegistry.Clear();
        }

        public static string BuildScopeKey(string playlistId, MediaType mediaType, string sourceKey, ulong datasetFingerprint)
        {
            string p = string.IsNullOrWhiteSpace(playlistId) ? "default" : playlistId.Trim();
            string s = string.IsNullOrWhiteSpace(sourceKey) ? "Unknown" : sourceKey.Trim();
            return $"{p}|{mediaType}|{s}|{datasetFingerprint:X16}";
        }
    }
}
