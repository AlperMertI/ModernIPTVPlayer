using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Project Zero Persistent Match Indexer.
    /// Maps significant title tokens to local record indices for instant library matching.
    /// </summary>
    public class StreamMatchIndexer
    {
        private Dictionary<string, int[]> _tokenMap = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int[]> _idMap = new(StringComparer.OrdinalIgnoreCase); // IMDbId -> record indices
        private const int CurrentVersion = 5;
        private const int MaxIndexKeys = 1_000_000;
        private const int MaxIndicesPerKey = 1_000_000;
        private readonly object _syncRoot = new();
        private long _sourceFingerprint = 0;
        private bool _isLoaded = false;

        public bool IsLoaded => _isLoaded;
        public long SourceFingerprint => _sourceFingerprint;
        public int TokenCount { get { lock (_syncRoot) return _tokenMap.Count; } }
        public int IdCount { get { lock (_syncRoot) return _idMap.Count; } }

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

        public void Build(VirtualVodList vvl)
        {
            var rawTokenMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var rawIdMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var session = vvl.GetSession(); 
            
            for (int i = 0; i < vvl.Count; i++)
            {
                if (!session.TryReadRecord<Models.Metadata.VodRecord>(i, out var record)) continue;

                // 1. IMDb ID
                string imdb = session.GetString(record.ImdbIdOff, record.ImdbIdLen);
                if (!string.IsNullOrEmpty(imdb))
                {
                    if (!rawIdMap.TryGetValue(imdb, out var idList)) rawIdMap[imdb] = idList = new List<int>();
                    idList.Add(i);
                }

                // 2. Title
                string name = session.GetString(record.NameOff, record.NameLen);
                var tokens = TitleHelper.GetSignificantTokens(name);
                foreach (var token in tokens)
                {
                    if (!rawTokenMap.TryGetValue(token, out var list)) rawTokenMap[token] = list = new List<int>();
                    list.Add(i);
                }
            }

            Commit(rawTokenMap, rawIdMap, vvl.Fingerprint);
        }

        public void Build(VirtualSeriesList vsl)
        {
            var rawTokenMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var rawIdMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var session = vsl.GetSession();

            for (int i = 0; i < vsl.Count; i++)
            {
                if (!session.TryReadRecord<Models.Metadata.SeriesRecord>(i, out var record)) continue;

                // 1. IMDb ID
                string imdb = session.GetString(record.ImdbIdOff, record.ImdbIdLen);
                if (!string.IsNullOrEmpty(imdb))
                {
                    if (!rawIdMap.TryGetValue(imdb, out var idList)) rawIdMap[imdb] = idList = new List<int>();
                    idList.Add(i);
                }

                // 2. Name
                string name = session.GetString(record.NameOff, record.NameLen);
                var tokens = TitleHelper.GetSignificantTokens(name);
                foreach (var token in tokens)
                {
                    if (!rawTokenMap.TryGetValue(token, out var list)) rawTokenMap[token] = list = new List<int>();
                    list.Add(i);
                }
            }

            Commit(rawTokenMap, rawIdMap, vsl.Fingerprint);
        }

        private void Commit(Dictionary<string, List<int>> rawTokenMap, Dictionary<string, List<int>> rawIdMap, long fingerprint)
        {
            lock (_syncRoot)
            {
                _tokenMap = PackIndex(rawTokenMap);
                _idMap = PackIndex(rawIdMap);
                _sourceFingerprint = fingerprint;
                _isLoaded = true;
            }
        }

        private static Dictionary<string, int[]> PackIndex(Dictionary<string, List<int>> raw)
        {
            var packed = new Dictionary<string, int[]>(raw.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in raw)
            {
                int[] values = kvp.Value.ToArray();
                if (values.Length > 1)
                {
                    Array.Sort(values);
                    int unique = 1;
                    for (int i = 1; i < values.Length; i++)
                    {
                        if (values[i] != values[unique - 1])
                        {
                            values[unique++] = values[i];
                        }
                    }

                    if (unique != values.Length)
                    {
                        Array.Resize(ref values, unique);
                    }
                }

                packed[kvp.Key] = values;
            }

            return packed;
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

        public int[] FindByToken(string? token)
        {
            if (string.IsNullOrEmpty(token) || !_isLoaded) return Array.Empty<int>();
            lock (_syncRoot)
            {
                return _tokenMap.TryGetValue(token, out int[] ids) ? ids : Array.Empty<int>();
            }
        }

        public void AddId(string imdbId, int recordIndex)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || recordIndex < 0) return;

            lock (_syncRoot)
            {
                if (!_idMap.TryGetValue(imdbId, out var ids))
                {
                    _idMap[imdbId] = new[] { recordIndex };
                    _isLoaded = true;
                    return;
                }

                if (Array.IndexOf(ids, recordIndex) >= 0) return;

                var updated = new int[ids.Length + 1];
                Array.Copy(ids, updated, ids.Length);
                updated[ids.Length] = recordIndex;
                Array.Sort(updated);
                _idMap[imdbId] = updated;
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

                    result = result == null ? ids : IntersectSorted(result, ids);

                    if (result.Length == 0) break;
                }
            }

            return result ?? Array.Empty<int>();
        }

        private static int[] IntersectSorted(int[] left, int[] right)
        {
            if (left.Length == 0 || right.Length == 0) return Array.Empty<int>();

            var result = new List<int>(Math.Min(left.Length, right.Length));
            int i = 0;
            int j = 0;
            while (i < left.Length && j < right.Length)
            {
                int a = left[i];
                int b = right[j];
                if (a == b)
                {
                    result.Add(a);
                    i++;
                    j++;
                }
                else if (a < b) i++;
                else j++;
            }

            return result.Count == 0 ? Array.Empty<int>() : result.ToArray();
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
                    bw.Write(CurrentVersion);
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
                return await Task.Run(() => LoadCore(filePath));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MatchIndex] Failed to load index '{Path.GetFileName(filePath)}': {ex.Message}");
                return false;
            }
        }

        private bool LoadCore(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs, Encoding.UTF8);

            if (br.ReadInt32() != 0x4D5A4958) return false;
            int version = br.ReadInt32();
            if (version != CurrentVersion) return false;

            long loadedFingerprint = br.ReadInt64();

            if (!TryReadCount(br, MaxIndexKeys, "token keys", out int tokenCount)) return false;
            var newTokenMap = new Dictionary<string, int[]>(tokenCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tokenCount; i++)
            {
                string token = br.ReadString();
                if (!TryReadCount(br, MaxIndicesPerKey, "token indices", out int idCount)) return false;

                int[] ids = new int[idCount];
                for (int j = 0; j < idCount; j++) ids[j] = br.ReadInt32();
                newTokenMap[token] = ids;
            }

            if (!TryReadCount(br, MaxIndexKeys, "id keys", out int imdbCount)) return false;
            var newIdMap = new Dictionary<string, int[]>(imdbCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < imdbCount; i++)
            {
                string imdbId = br.ReadString();
                if (!TryReadCount(br, MaxIndicesPerKey, "id indices", out int subCount)) return false;

                int[] subIds = new int[subCount];
                for (int j = 0; j < subCount; j++) subIds[j] = br.ReadInt32();
                newIdMap[imdbId] = subIds;
            }

            lock (_syncRoot)
            {
                _tokenMap = newTokenMap;
                _idMap = newIdMap;
                _sourceFingerprint = loadedFingerprint;
                _isLoaded = true;
            }

            return true;
        }

        private static bool TryReadCount(BinaryReader br, int max, string label, out int count)
        {
            count = br.ReadInt32();
            if ((uint)count <= (uint)max) return true;

            AppLogger.Warn($"[MatchIndex] Invalid {label} count in binary index: {count}");
            count = 0;
            return false;
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

    }
}
