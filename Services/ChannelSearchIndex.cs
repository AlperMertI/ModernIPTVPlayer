using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Phase A: Project Zero Channel Search Index.
    /// Tokenizes channel names at load time, enables O(1) search via token → index intersection.
    /// 
    /// "beIN Sports 1 HD" → tokens: ["bein", "sports", "1", "hd"]
    /// Search "bein sp" → tokens: ["bein", "sp"] → intersect indices → instant results
    /// </summary>
    public static class ChannelSearchIndex
    {
        // Token → sorted array of channel indices (within _allChannels)
        private static Dictionary<string, int[]> _tokenIndex = new(StringComparer.OrdinalIgnoreCase);
        private static bool _isBuilt = false;

        /// <summary>
        /// Builds the search index from a list of channel names.
        /// Called once after channels are loaded. ~50ms for 50k channels.
        /// </summary>
        public static void BuildIndex(IReadOnlyList<string> channelNames)
        {
            if (channelNames == null || channelNames.Count == 0)
            {
                _tokenIndex.Clear();
                _isBuilt = false;
                return;
            }

            var rawIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < channelNames.Count; i++)
            {
                var name = channelNames[i];
                if (string.IsNullOrEmpty(name)) continue;

                var tokens = GetChannelTokens(name);
                foreach (var token in tokens)
                {
                    if (!rawIndex.TryGetValue(token, out var list))
                        rawIndex[token] = list = new List<int>();
                    list.Add(i);
                }
            }

            // Convert lists to sorted arrays for efficient intersection
            _tokenIndex = new Dictionary<string, int[]>(rawIndex.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in rawIndex)
            {
                kvp.Value.Sort();
                _tokenIndex[kvp.Key] = kvp.Value.ToArray();
            }

            _isBuilt = true;
        }

        /// <summary>
        /// Searches the index for the given query.
        /// Returns channel indices matching ALL query tokens (AND logic).
        /// Supports prefix matching: "sk" matches "sky", "sports", etc.
        /// </summary>
        public static int[] Search(string query)
        {
            if (!_isBuilt || string.IsNullOrWhiteSpace(query))
                return Array.Empty<int>();

            var tokens = GetQueryTokens(query);
            if (tokens.Count == 0)
                return Array.Empty<int>();

            // For each query token, collect candidate indices
            // If exact match → use directly
            // If no exact match → find all tokens that START with this token (prefix match)
            List<int>[] candidateSets = new List<int>[tokens.Count];
            int tokenIdx = 0;

            foreach (var token in tokens)
            {
                if (_tokenIndex.TryGetValue(token, out var exactIndices))
                {
                    // Exact match found
                    candidateSets[tokenIdx] = new List<int>(exactIndices);
                }
                else
                {
                    // Prefix match: find all index tokens starting with this query token
                    var matched = new List<int>();
                    foreach (var kvp in _tokenIndex)
                    {
                        if (kvp.Key.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                        {
                            matched.AddRange(kvp.Value);
                        }
                    }

                    if (matched.Count == 0) return Array.Empty<int>(); // No prefix match at all

                    // Deduplicate and sort
                    matched.Sort();
                    var deduped = new List<int>(matched.Count);
                    for (int i = 0; i < matched.Count; i++)
                    {
                        if (i == 0 || matched[i] != matched[i - 1])
                            deduped.Add(matched[i]);
                    }
                    candidateSets[tokenIdx] = deduped;
                }

                tokenIdx++;
            }

            // Intersect all candidate sets
            int[] result = null;
            foreach (var set in candidateSets)
            {
                if (set == null || set.Count == 0) return Array.Empty<int>();

                if (result == null)
                {
                    result = set.ToArray();
                }
                else
                {
                    result = IntersectSorted(result, set.ToArray());
                }

                if (result.Length == 0) return Array.Empty<int>();
            }

            return result ?? Array.Empty<int>();
        }

        /// <summary>
        /// Tokenizes a channel name for indexing.
        /// Lowercase, split by non-alphanumeric, keep all tokens.
        /// </summary>
        private static HashSet<string> GetChannelTokens(string name)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(name.Length);

            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());

            return tokens;
        }

        /// <summary>
        /// Tokenizes a search query for lookup.
        /// Each part becomes a token for intersection.
        /// </summary>
        private static HashSet<string> GetQueryTokens(string query)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            query = query.ToLowerInvariant().Trim();

            var parts = query.Split(new[] { ' ', '-', '_', '.', '|', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                tokens.Add(part);
            }

            return tokens;
        }

        /// <summary>
        /// Intersects two sorted arrays efficiently.
        /// Both input arrays must be sorted ascending.
        /// </summary>
        private static int[] IntersectSorted(int[] a, int[] b)
        {
            if (a.Length == 0 || b.Length == 0) return Array.Empty<int>();

            // Use a pooled list for the result
            var result = new List<int>(Math.Min(a.Length, b.Length));
            int i = 0, j = 0;

            while (i < a.Length && j < b.Length)
            {
                if (a[i] == b[j]) { result.Add(a[i]); i++; j++; }
                else if (a[i] < b[j]) i++;
                else j++;
            }

            return result.ToArray();
        }

        /// <summary>
        /// Clears the index. Called when channels are reloaded.
        /// </summary>
        public static void Clear()
        {
            _tokenIndex.Clear();
            _isBuilt = false;
        }

        /// <summary>
        /// Whether the index is currently built.
        /// </summary>
        public static bool IsBuilt => _isBuilt;

        /// <summary>
        /// Returns all indices if the query is empty, or matching indices otherwise.
        /// This is the main entry point for search filtering.
        /// </summary>
        public static int[] GetMatchingIndices(string query, int maxIndex)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                // Return all indices
                var all = new int[maxIndex];
                for (int i = 0; i < maxIndex; i++) all[i] = i;
                return all;
            }

            return Search(query);
        }
    }
}
