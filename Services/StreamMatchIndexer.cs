using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Project Zero Persistent Match Indexer.
    /// Maps significant title tokens to local stream IDs for instant library matching.
    /// </summary>
    public class StreamMatchIndexer
    {
        private Dictionary<string, int[]> _tokenMap = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int[]> _idMap = new(StringComparer.OrdinalIgnoreCase); // IMDbId -> StreamIds[]
        private readonly object _syncRoot = new();
        private long _sourceFingerprint = 0;
        private bool _isLoaded = false;

        public bool IsLoaded => _isLoaded;
        public long SourceFingerprint => _sourceFingerprint;

        /// <summary>
        /// Builds the index from a collection of streams using TitleHelper normalization.
        /// </summary>
        public void Build(IEnumerable<IMediaStream> streams)
        {
            Build(streams, 0); // Fingerprint not available for generic IEnumerable
        }

        public void Build(IEnumerable<IMediaStream> streams, long fingerprint)
        {
            var rawTokenMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var rawIdMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var stream in streams)
            {
                if (stream == null) continue;
                
                // 1. Index IMDb ID (Fastest Match)
                if (!string.IsNullOrEmpty(stream.IMDbId))
                {
                    if (!rawIdMap.TryGetValue(stream.IMDbId, out var idList))
                    {
                        rawIdMap[stream.IMDbId] = idList = new List<int>();
                    }
                    idList.Add(stream.Id);
                }

                // 2. Index Title Tokens (Fallback Match)
                var tokens = TitleHelper.GetSignificantTokens(stream.Title);
                foreach (var token in tokens)
                {
                    if (!rawTokenMap.TryGetValue(token, out var list))
                    {
                        rawTokenMap[token] = list = new List<int>();
                    }
                    list.Add(stream.Id);
                }
            }

            Commit(rawTokenMap, rawIdMap, fingerprint);
        }

        public unsafe void Build(VirtualVodList vvl)
        {
            var rawTokenMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var rawIdMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var session = vvl.GetSession(); 
            
            for (int i = 0; i < vvl.Count; i++)
            {
                var record = session.GetRecordPointer<Models.Metadata.VodRecord>(i);
                if (record == null) continue;

                // 1. IMDb ID
                string imdb = session.GetString(record->ImdbIdOff, record->ImdbIdLen);
                if (!string.IsNullOrEmpty(imdb))
                {
                    if (!rawIdMap.TryGetValue(imdb, out var idList)) rawIdMap[imdb] = idList = new List<int>();
                    idList.Add(record->StreamId);
                }

                // 2. Title
                string name = session.GetString(record->NameOff, record->NameLen);
                var tokens = TitleHelper.GetSignificantTokens(name);
                foreach (var token in tokens)
                {
                    if (!rawTokenMap.TryGetValue(token, out var list)) rawTokenMap[token] = list = new List<int>();
                    list.Add(record->StreamId);
                }
            }

            Commit(rawTokenMap, rawIdMap, vvl.Fingerprint);
        }

        public unsafe void Build(VirtualSeriesList vsl)
        {
            var rawTokenMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var rawIdMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var session = vsl.GetSession();

            for (int i = 0; i < vsl.Count; i++)
            {
                var record = session.GetRecordPointer<Models.Metadata.SeriesRecord>(i);
                if (record == null) continue;

                // 1. IMDb ID
                string imdb = session.GetString(record->ImdbIdOff, record->ImdbIdLen);
                if (!string.IsNullOrEmpty(imdb))
                {
                    if (!rawIdMap.TryGetValue(imdb, out var idList)) rawIdMap[imdb] = idList = new List<int>();
                    idList.Add(record->SeriesId);
                }

                // 2. Name
                string name = session.GetString(record->NameOff, record->NameLen);
                var tokens = TitleHelper.GetSignificantTokens(name);
                foreach (var token in tokens)
                {
                    if (!rawTokenMap.TryGetValue(token, out var list)) rawTokenMap[token] = list = new List<int>();
                    list.Add(record->SeriesId);
                }
            }

            Commit(rawTokenMap, rawIdMap, vsl.Fingerprint);
        }

        private void Commit(Dictionary<string, List<int>> rawTokenMap, Dictionary<string, List<int>> rawIdMap, long fingerprint)
        {
            lock (_syncRoot)
            {
                _tokenMap = rawTokenMap.ToDictionary(k => k.Key, v => v.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
                _idMap = rawIdMap.ToDictionary(k => k.Key, v => v.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
                _sourceFingerprint = fingerprint;
                _isLoaded = true;
            }
        }

        /// <summary>
        /// Stage 1: Fast lookup by IMDb ID.
        /// </summary>
        public int[] FindById(string? imdbId)
        {
            if (string.IsNullOrEmpty(imdbId) || !_isLoaded) return Array.Empty<int>();
            lock (_syncRoot)
            {
                return _idMap.TryGetValue(imdbId, out int[] ids) ? ids : Array.Empty<int>();
            }
        }

        /// <summary>
        /// Stage 2: Fallback lookup by query tokens.
        /// Uses INTERSECTION (AND logic) for precision.
        /// </summary>
        public int[] FindByTokens(HashSet<string> queryTokens)
        {
            if (!_isLoaded || queryTokens == null || queryTokens.Count == 0) return Array.Empty<int>();

            int[] result = null;

            lock (_syncRoot)
            {
                foreach (var token in queryTokens)
                {
                    if (!_tokenMap.TryGetValue(token, out var ids))
                    {
                        return Array.Empty<int>();
                    }

                    if (result == null) result = ids;
                    else result = result.Intersect(ids).ToArray();

                    if (result.Length == 0) break;
                }
            }

            return result ?? Array.Empty<int>();
        }

        /// <summary>
        /// Saves the index to a high-performance binary file.
        /// Includes Token Map and ID Map.
        /// </summary>
        public async Task SaveAsync(string filePath)
        {
            await Task.Run(() =>
            {
                lock (_syncRoot)
                {
                    using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                    using var bw = new BinaryWriter(fs, Encoding.UTF8);

                    // Header
                    bw.Write(0x4D5A4958); // 'MZIX'
                    bw.Write(4);          // Version 4 (Multi-source IMDb ID support)
                    bw.Write(_sourceFingerprint);
                    
                    // 1. Token Map
                    bw.Write(_tokenMap.Count);
                    foreach (var kvp in _tokenMap)
                    {
                        bw.Write(kvp.Key);
                        bw.Write(kvp.Value.Length);
                        foreach (var id in kvp.Value) bw.Write(id);
                    }

                    // 2. ID Map
                    bw.Write(_idMap.Count);
                    foreach (var kvp in _idMap)
                    {
                        bw.Write(kvp.Key); // IMDbId
                        bw.Write(kvp.Value.Length);
                        foreach (var id in kvp.Value) bw.Write(id);
                    }
                }
            });
        }

        /// <summary>
        /// Loads the index from a binary file in milliseconds.
        /// </summary>
        public async Task<bool> LoadAsync(string filePath)
        {
            if (!File.Exists(filePath)) return false;

            try
            {
                await Task.Run(() =>
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                    using var br = new BinaryReader(fs, Encoding.UTF8);

                    if (br.ReadInt32() != 0x4D5A4958) return;
                    int version = br.ReadInt32();
                    long loadedFingerprint = (version >= 3) ? br.ReadInt64() : 0;
                    
                    // Load Token Map
                    int tokenCount = br.ReadInt32();
                    var newTokenMap = new Dictionary<string, int[]>(tokenCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < tokenCount; i++)
                    {
                        string token = br.ReadString();
                        int idCount = br.ReadInt32();
                        int[] ids = new int[idCount];
                        for (int j = 0; j < idCount; j++) ids[j] = br.ReadInt32();
                        newTokenMap[token] = ids;
                    }

                    // Load ID Map (Version 2+)
                    var newIdMap = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                    if (version >= 2)
                    {
                        int idCount = br.ReadInt32();
                        for (int i = 0; i < idCount; i++)
                        {
                            string imdbId = br.ReadString();
                            if (version >= 4)
                            {
                                int subCount = br.ReadInt32();
                                int[] subIds = new int[subCount];
                                for (int j = 0; j < subCount; j++) subIds[j] = br.ReadInt32();
                                newIdMap[imdbId] = subIds;
                            }
                            else
                            {
                                int streamId = br.ReadInt32();
                                newIdMap[imdbId] = new[] { streamId };
                            }
                        }
                    }

                    lock (_syncRoot)
                    {
                        _tokenMap = newTokenMap;
                        _idMap = newIdMap;
                        _sourceFingerprint = loadedFingerprint;
                        _isLoaded = true;
                    }
                });
                return true;
            }
            catch { return false; }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _tokenMap.Clear();
                _idMap.Clear();
                _sourceFingerprint = 0;
                _isLoaded = false;
            }
        }

        public Dictionary<string, int[]> GetTokenMap()
        {
            lock (_syncRoot) return new Dictionary<string, int[]>(_tokenMap, StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, int[]> GetIdMap()
        {
            lock (_syncRoot) return new Dictionary<string, int[]>(_idMap, StringComparer.OrdinalIgnoreCase);
        }
    }
}
