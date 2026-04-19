using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Services.Json;

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
        private const string MAPPING_FILE = "IdMappings.bin.zst";
        private const uint MAGIC = 0x49444D31; // IDM1
        private bool _isDirty = false;
        private Timer _saveTimer;
        private readonly string _dataFolderPath;

        private IdMappingService()
        {
            _dataFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernIPTVPlayer");
            Directory.CreateDirectory(_dataFolderPath);
            
            _ = LoadAsync();
            _saveTimer = new Timer(async _ => await SaveIfDirtyAsync(), null, -1, -1);
        }

        private async Task LoadAsync()
        {
            try
            {
                await _fileLock.WaitAsync();
                var filePath = Path.Combine(_dataFolderPath, MAPPING_FILE);

                if (File.Exists(filePath))
                {
                    using var fs = File.OpenRead(filePath);
                    using var decompressor = new ZstdSharp.DecompressionStream(fs);
                    using var reader = new BinaryReader(decompressor, System.Text.Encoding.UTF8);

                    if (reader.ReadUInt32() != MAGIC) return;

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        string imdb = reader.ReadString();
                        string tmdb = reader.ReadString();
                        _imdbToTmdb[imdb] = tmdb;
                        _tmdbToImdb[tmdb] = imdb;
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
                var filePath = Path.Combine(_dataFolderPath, MAPPING_FILE);
                
                var snapshot = _imdbToTmdb.ToList();
                using (var fs = File.Create(filePath))
                using (var compressor = new ZstdSharp.CompressionStream(fs, 3))
                using (var writer = new BinaryWriter(compressor, System.Text.Encoding.UTF8))
                {
                    writer.Write(MAGIC);
                    writer.Write(snapshot.Count);
                    foreach (var kvp in snapshot)
                    {
                        writer.Write(kvp.Key);
                        writer.Write(kvp.Value);
                    }
                }
                _isDirty = false;
            }
            catch { /* Silent fail */ }
            finally { _fileLock.Release(); }
        }

        public void RegisterMapping(string imdbId, string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || string.IsNullOrWhiteSpace(tmdbId)) return;
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
                _saveTimer.Change(10000, -1); // Save in 10s
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

            string normA = idA.Replace("tmdb:", "").Trim();
            string normB = idB.Replace("tmdb:", "").Trim();

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
