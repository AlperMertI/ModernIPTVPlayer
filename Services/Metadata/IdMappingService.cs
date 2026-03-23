using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace ModernIPTVPlayer.Services.Metadata
{
    /// <summary>
    /// Persistent registry for ID cross-referencing (IMDb <-> TMDB).
    /// Used for stable identity resolution during search merging and metadata enrichment.
    /// </summary>
    public class IdMappingService
    {
        private static Lazy<IdMappingService> _instance = new(() => new IdMappingService());
        public static IdMappingService Instance => _instance.Value;

        // Key: IMDb ID (tt...), Value: TMDB ID (numeric string)
        private ConcurrentDictionary<string, string> _imdbToTmdb = new(StringComparer.OrdinalIgnoreCase);
        // Key: TMDB ID, Value: IMDb ID
        private ConcurrentDictionary<string, string> _tmdbToImdb = new(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private const string MAPPING_FILE = "IdMappings.json";
        private bool _isDirty = false;
        private Timer _saveTimer;

        private IdMappingService()
        {
            _ = LoadAsync();
            _saveTimer = new Timer(async _ => await SaveIfDirtyAsync(), null, -1, -1);
        }

        private async Task LoadAsync()
        {
            try
            {
                await _fileLock.WaitAsync();
                var folder = ApplicationData.Current.LocalFolder;
                if (await folder.TryGetItemAsync(MAPPING_FILE) is StorageFile file)
                {
                    using var stream = await file.OpenStreamForReadAsync();
                    var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream);
                    if (loaded != null)
                    {
                        foreach (var kvp in loaded)
                        {
                            _imdbToTmdb[kvp.Key] = kvp.Value;
                            _tmdbToImdb[kvp.Value] = kvp.Key;
                        }
                    }
                }
            }
            catch { /* Silent fail for cache */ }
            finally { _fileLock.Release(); }
        }

        private async Task SaveIfDirtyAsync()
        {
            if (!_isDirty) return;
            try
            {
                await _fileLock.WaitAsync();
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(MAPPING_FILE, CreationCollisionOption.ReplaceExisting);
                using var stream = await file.OpenStreamForWriteAsync();
                var snapshot = _imdbToTmdb.ToDictionary(k => k.Key, v => v.Value);
                await JsonSerializer.SerializeAsync(stream, snapshot);
                _isDirty = false;
            }
            catch { /* Silent fail */ }
            finally { _fileLock.Release(); }
        }

        public void RegisterMapping(string imdbId, string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(tmdbId)) return;
            
            // Normalize
            if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return;

            bool changed = false;
            if (!_imdbToTmdb.TryGetValue(imdbId, out var existingTmdb) || existingTmdb != tmdbId)
            {
                _imdbToTmdb[imdbId] = tmdbId;
                _tmdbToImdb[tmdbId] = imdbId;
                changed = true;
            }

            if (changed)
            {
                _isDirty = true;
                _saveTimer.Change(5000, -1); // Save in 5s
            }
        }

        public string GetImdbForTmdb(string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(tmdbId)) return null;
            return _tmdbToImdb.TryGetValue(tmdbId, out var imdbId) ? imdbId : null;
        }

        public string GetTmdbForImdb(string imdbId)
        {
            if (string.IsNullOrWhiteSpace(imdbId)) return null;
            return _imdbToTmdb.TryGetValue(imdbId, out var tmdbId) ? tmdbId : null;
        }

        public bool AreIdentical(string idA, string idB)
        {
            if (string.IsNullOrWhiteSpace(idA) || string.IsNullOrWhiteSpace(idB)) return false;
            if (string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase)) return true;

            // Normalize IDs to simple formats (tt... or tmdb_id)
            string normA = idA.Replace("tmdb:", "").Trim();
            string normB = idB.Replace("tmdb:", "").Trim();

            // Try direct mapping
            if (normA.StartsWith("tt") && !normB.StartsWith("tt"))
                return GetTmdbForImdb(normA) == normB;
            
            if (normB.StartsWith("tt") && !normA.StartsWith("tt"))
                return GetTmdbForImdb(normB) == normA;

            return false;
        }

        public void Clear()
        {
            _imdbToTmdb.Clear();
            _tmdbToImdb.Clear();
            _isDirty = true;
            _ = SaveIfDirtyAsync();
        }
    }
}
