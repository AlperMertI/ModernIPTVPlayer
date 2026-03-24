using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services.Iptv
{
    public class IptvMatchService
    {
        private static IptvMatchService _instance;
        public static IptvMatchService Instance => _instance ??= new IptvMatchService();

        private List<IMediaStream> _vods = new();
        private List<IMediaStream> _series = new();
        private Dictionary<string, List<IMediaStream>> _imdbIndex = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<IMediaStream>> _tokenIndex = new(StringComparer.OrdinalIgnoreCase);
        private bool _isInitialized = false;
        private readonly object _indexLock = new();

        private IptvMatchService() { }

        /// <summary>
        /// Updates the internal stream lists and indices atomically.
        /// indices are pre-built by ContentCacheService in a background thread.
        /// </summary>
        public void UpdateIndices(IEnumerable<IMediaStream> vods, IEnumerable<IMediaStream> series, 
            Dictionary<string, List<IMediaStream>> imdbIndex, Dictionary<string, List<IMediaStream>> tokenIndex)
        {
            lock (_indexLock)
            {
                _vods = vods?.ToList() ?? new List<IMediaStream>();
                _series = series?.ToList() ?? new List<IMediaStream>();
                _imdbIndex = imdbIndex ?? new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
                _tokenIndex = tokenIndex ?? new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
                
                _isInitialized = true;
                TitleHelper.ClearCaches();
                AppLogger.Info($"[IptvMatchService] Indices updated. VODs: {_vods.Count}, Series: {_series.Count}, Imdb/Tmdb Index: {_imdbIndex.Count}, Tokens: {_tokenIndex.Count}");
            }
        }

        // Backward compatibility
        public void Initialize(IEnumerable<IMediaStream> vods, IEnumerable<IMediaStream> series)
        {
             // This is now handled more efficiently via UpdateIndices from ContentCacheService.
             // But if called directly, we'll do a basic build.
             var imdbIndex = new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
             foreach (var item in (vods ?? Enumerable.Empty<IMediaStream>()).Concat(series ?? Enumerable.Empty<IMediaStream>()))
             {
                 if (string.IsNullOrEmpty(item.IMDbId)) continue;
                 
                 // Support both "tt..." (IMDb) and numeric (TMDB) IDs
                 if (!imdbIndex.TryGetValue(item.IMDbId, out var list)) { list = new List<IMediaStream>(); imdbIndex[item.IMDbId] = list; }
                 list.Add(item);
             }

             var tokenIndex = new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
             foreach (var item in (vods ?? Enumerable.Empty<IMediaStream>()).Concat(series ?? Enumerable.Empty<IMediaStream>()))
             {
                 if (string.IsNullOrEmpty(item.Title)) continue;
                 var tokens = TitleHelper.GetSignificantTokens(item.Title);
                 foreach (var t in tokens)
                 {
                     if (!tokenIndex.TryGetValue(t, out var list)) { list = new List<IMediaStream>(); tokenIndex[t] = list; }
                     list.Add(item);
                 }
             }

             UpdateIndices(vods, series, imdbIndex, tokenIndex);
        }

        public bool InstanceInitializeCheck() => _isInitialized;

        public IMediaStream? FindMatch(string? title, string? originalTitle, string? subTitle, string? localizedTitle, string? year, string? query, bool isSeries, System.Threading.CancellationToken ct = default)
        {
            return FindAllMatches(title, originalTitle, subTitle, localizedTitle, year, query, isSeries, false, ct).FirstOrDefault();
        }

        public List<IMediaStream> FindAllMatches(string? title, string? originalTitle, string? subTitle, string? localizedTitle, string? year, string? query, bool isSeries, bool stopOnHighConfidence = false, System.Threading.CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) {
                System.Diagnostics.Debug.WriteLine($"[IptvMatch] CANCELLED before start for Query: {query ?? title}");
                ct.ThrowIfCancellationRequested();
            }

            var searchTitles = new[] { title, originalTitle, subTitle, localizedTitle, query }
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();

            string searchType = isSeries ? "SERIES" : "VOD";
            AppLogger.Warn($"[IptvMatch] Finding matches for {searchType}: '{title}' (Year: {year ?? "null"}). Index Status: Initialized={_isInitialized}, Tokens={_tokenIndex.Count}, VODs={_vods.Count}, Series={_series.Count}");
            if (searchTitles.Count > 1) AppLogger.Warn($"[IptvMatch] Search Titles: {string.Join(", ", searchTitles)}");

            // 1. Get Base Tokens for initial candidate filtering (real words only, no composites)
            var searchBaseTokens = searchTitles.SelectMany(t => TitleHelper.GetBaseTokens(t)).Distinct().ToList();
            if (searchBaseTokens.Count == 0)
            {
                AppLogger.Warn($"[IptvMatch] No significant tokens found for search titles.");
                return new List<IMediaStream>();
            }

            AppLogger.Warn($"[IptvMatch] Candidate Filter Tokens (Base): {string.Join(", ", searchBaseTokens)}");

            // --- New Robust Candidate Collection ---
            var searchTokenSets = searchTitles.Select(t => new { 
                Title = t, 
                Tokens = TitleHelper.GetBaseTokens(t) 
            }).Where(s => s.Tokens.Count > 0).ToList();

            if (!searchTokenSets.Any()) return new List<IMediaStream>();

            string targetYearStr = !string.IsNullOrEmpty(year) ? TitleHelper.ExtractYear(year) : "";
            var candidates = new HashSet<IMediaStream>();
            
            // Map each item to its match count against AT LEAST ONE search title
            var itemScores = new Dictionary<IMediaStream, int>();
            foreach (var set in searchTokenSets)
            {
                var titleLevelMatches = new Dictionary<IMediaStream, int>();
                foreach (var token in set.Tokens)
                {
                    ct.ThrowIfCancellationRequested();
                    if (token.Length < 2 && !char.IsDigit(token[0])) continue;
                    if (_tokenIndex.TryGetValue(token, out var matches))
                    {
                        foreach (var m in matches)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (m.Type != (isSeries ? "series" : "movie")) continue;
                            if (titleLevelMatches.ContainsKey(m)) titleLevelMatches[m]++;
                            else titleLevelMatches[m] = 1;
                        }
                    }
                }

                // Evaluation for this Title Set
                foreach (var kvp in titleLevelMatches)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = kvp.Key;
                    int matchCount = kvp.Value;
                    int targetCount = set.Tokens.Count;

                    // [PERFORMANCE] Cap candidates at 300 to prevent runaway fuzzy matching for short queries
                    if (candidates.Count >= 300) break;

                    bool isStrictMatch = matchCount == targetCount;
                    bool isYearMatch = false;

                    if (!isStrictMatch && !string.IsNullOrEmpty(targetYearStr))
                    {
                        string itemYear = TitleHelper.ExtractYear(item.Year);
                        if (string.IsNullOrEmpty(itemYear)) itemYear = TitleHelper.ExtractYear(item.Title);
                        
                        // [NEW] Year-First Fallback: If year matches exactly, allow 2 tokens (or all if < 2)
                        if (itemYear == targetYearStr)
                        {
                            int minRequired = Math.Min(2, targetCount);
                            if (matchCount >= minRequired) isYearMatch = true;
                        }
                    }

                    if (isStrictMatch || isYearMatch)
                    {
                         candidates.Add(item);
                    }
                }
            }

            // [NEW] Category Fallback
            // If primary category search finds nothing, look in the other category too.
            if (!candidates.Any())
            {
                var fallbackType = isSeries ? "movie" : "series";
                AppLogger.Warn($"[IptvMatch] Search yielded 0 candidates in primary category ({searchType}). Trying fallback search in category: {fallbackType.ToUpperInvariant()}...");
                
                foreach (var set in searchTokenSets)
                {
                    var titleLevelMatches = new Dictionary<IMediaStream, int>();
                    foreach (var token in set.Tokens)
                    {
                        if (token.Length < 2 && !char.IsDigit(token[0])) continue;
                        if (_tokenIndex.TryGetValue(token, out var matches))
                        {
                            foreach (var m in matches)
                            {
                                if (m.Type != fallbackType) continue;
                                if (titleLevelMatches.ContainsKey(m)) titleLevelMatches[m]++;
                                else titleLevelMatches[m] = 1;
                            }
                        }
                    }

                    foreach (var kvp in titleLevelMatches)
                    {
                        ct.ThrowIfCancellationRequested();
                        var item = kvp.Key;
                        int matchCount = kvp.Value;
                        int targetCount = set.Tokens.Count;
                        
                        if (candidates.Count >= 200) break;

                        bool isStrictMatch = matchCount == targetCount;
                        bool isYearMatch = false;
                        if (!isStrictMatch && !string.IsNullOrEmpty(targetYearStr))
                        {
                            string itemYear = TitleHelper.ExtractYear(item.Year);
                            if (string.IsNullOrEmpty(itemYear)) itemYear = TitleHelper.ExtractYear(item.Title);
                            if (itemYear == targetYearStr && matchCount >= Math.Min(2, targetCount))
                                isYearMatch = true;
                        }

                        if (isStrictMatch || isYearMatch) candidates.Add(item);
                    }
                }
            }

            if (!candidates.Any())
            {
                AppLogger.Warn($"[IptvMatch] Found 0 candidates by token intersection for: {string.Join(", ", searchTitles)}");
                return new List<IMediaStream>();
            }

            AppLogger.Warn($"[IptvMatch] Found {candidates.Count} candidate(s) via token intersection (Strict or Year-Lenient).");

            // [OPTIMIZATION] Pre-calculate tokens for candidates ONCE before scoring
            var candidateDatas = candidates.Select(c => new {
                Item = c,
                Tokens = TitleHelper.GetSignificantTokens(c.Title)
            }).ToList();

            // 2. Build scoring tokens
            var queryTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searchScoringSets = new List<(string Title, HashSet<string> Tokens, string Source, double Weight)>();

            if (!string.IsNullOrEmpty(originalTitle)) {
                var tokens = TitleHelper.GetSignificantTokens(originalTitle);
                searchScoringSets.Add((originalTitle, tokens, "Original", 1.2)); // 20% boost
                foreach(var t in tokens) queryTokens.Add(t);
            }
            if (!string.IsNullOrEmpty(title) && title != originalTitle) {
                var tokens = TitleHelper.GetSignificantTokens(title);
                searchScoringSets.Add((title, tokens, "Localized", 1.0));
                foreach(var t in tokens) queryTokens.Add(t);
            }
            if (!string.IsNullOrEmpty(localizedTitle) && localizedTitle != title && localizedTitle != originalTitle) {
                var tokens = TitleHelper.GetSignificantTokens(localizedTitle);
                searchScoringSets.Add((localizedTitle, tokens, "Localized", 1.1)); // 10% boost for explicit localized name
                foreach(var t in tokens) queryTokens.Add(t);
            }
            if (!string.IsNullOrEmpty(subTitle)) {
                var tokens = TitleHelper.GetSignificantTokens(subTitle);
                searchScoringSets.Add((subTitle, tokens, "Subtitle", 0.9)); // Slight penalty for subtitle matches
                foreach(var t in tokens) queryTokens.Add(t);
            }
            if (!string.IsNullOrEmpty(query)) {
                var tokens = TitleHelper.GetSignificantTokens(query);
                searchScoringSets.Add((query, tokens, "Query", 0.8)); // Query is least authoritative for 'isExact'
                foreach(var t in tokens) queryTokens.Add(t);
            }

            var scoredResults = new List<(IMediaStream Item, double Score, bool IsExact)>();

            foreach (var c in candidateDatas)
            {
                if (ct.IsCancellationRequested) {
                    System.Diagnostics.Debug.WriteLine($"[IptvMatch] CANCELLED during loop for Query: {query ?? title}");
                    ct.ThrowIfCancellationRequested();
                }

                // Match against all possible search titles, pick the best weighted score
                var matchResults = searchScoringSets.Select(s => {
                    bool isMatch = TitleHelper.IsMatch(c.Tokens, s.Tokens, c.Item.Title, s.Title, c.Item.Year, year);
                    double sim = TitleHelper.CalculateSimilarity(c.Item.Title, s.Title);
                    return new { 
                        Source = s.Source, 
                        IsMatch = isMatch, 
                        Score = sim * s.Weight,
                        Similarity = sim,
                        IsExactEligible = s.Source != "Query" // Only real titles count towards 'isExact'
                    };
                }).ToList();

                bool hasMatch = matchResults.Any(m => m.IsMatch);
                bool isExact = matchResults.Any(m => m.IsMatch && m.IsExactEligible);
                double bestSim = matchResults.Max(m => m.Score);
                var bestMatch = matchResults.OrderByDescending(m => m.Score).First();
                double maxSimilarity = matchResults.Max(m => m.Similarity);

                // Apply weighted ranking
                double finalScore = bestSim * 50;

                // Year Match (Highest priority)
                string y1 = TitleHelper.ExtractYear(year);
                string y2 = TitleHelper.ExtractYear(c.Item.Year);
                // Ensure y2 is not empty for logging/internal logic if possible
                if (string.IsNullOrEmpty(y2)) y2 = TitleHelper.ExtractYear(c.Item.Title);

                bool yearMatched = false;
                if (!string.IsNullOrEmpty(y1) && !string.IsNullOrEmpty(y2))
                {
                    if (y1 == y2) { finalScore += 100; yearMatched = true; }
                }

                // [STRICT FILTER] ONLY add if IsMatch returned true for at least one search title.
                // This ensures Year REJECT, Digit REJECT, etc. from TitleHelper are strictly respected.
                if (hasMatch)
                {
                    scoredResults.Add((c.Item, finalScore, isExact));
                    AppLogger.Warn($"[IptvMatch] Candidate Accepted: '{c.Item.Title}' (Year: {y2}, Type: {c.Item.Type}) | Score: {finalScore:F1}, Exact: {isExact}, YearMatch: {yearMatched}, BestSim: {bestMatch.Similarity:F2} (via {bestMatch.Source})");
                    
                    // [OPTIMIZATION] High-Confidence Early Exit (0.95+ similarity OR Exact ID Match)
                    if (stopOnHighConfidence && maxSimilarity >= 0.95)
                    {
                        AppLogger.Warn($"[IptvMatch] High-confidence match found ({maxSimilarity:F2}). Stopping scan.");
                        break;
                    }
                }
                else if (bestSim > 0.4)
                {
                    AppLogger.Warn($"[IptvMatch] Candidate Rejected: '{c.Item.Title}' (Year: {y2}) | BestSim: {bestMatch.Similarity:F2} (via {bestMatch.Source}), YearMatch: {yearMatched} (Similarity ok but Filter failed)");
                }

                // [OPTIMIZATION] High-Confidence Early Exit (0.95+ similarity OR Exact ID Match)
                if (stopOnHighConfidence && maxSimilarity >= 0.95)
                {
                    AppLogger.Warn($"[IptvMatch] High-confidence match found ({maxSimilarity:F2}). Stopping scan.");
                    break;
                }
            }

            var rankedResults = scoredResults
                .OrderByDescending(x => x.IsExact)
                .ThenByDescending(x => x.Score)
                .Select(x => x.Item)
                .ToList();

            if (rankedResults.Count > 0)
            {
                var bestMatched = rankedResults[0];
                var bestScored = scoredResults.First(r => r.Item == bestMatched);
                AppLogger.Warn($"[IptvMatch] Selected Best Match: '{bestMatched.Title}' (Year: {bestMatched.Year}) for '{title}' (Exact: {bestScored.IsExact}, Score: {bestScored.Score:F1})");
            }
            else if (scoredResults.Any())
            {
                var bestRejected = scoredResults.OrderByDescending(r => r.Score).First();
                AppLogger.Warn($"[IptvMatch] Match REJECTED for '{title}': Top candidate '{bestRejected.Item.Title}' (Year: {bestRejected.Item.Year}) had score {bestRejected.Score:F1} but IsExact was {bestRejected.IsExact}");
            }
            else
            {
                 AppLogger.Warn($"[IptvMatch] All {candidates.Count} candidates rejected for '{title}'. (Similarity too low or Year mismatch)");
            }

            return rankedResults;
        }

        public IMediaStream? FindMatchById(string? imdbId, bool isSeries, bool stopOnHighConfidence = false)
        {
            return FindAllMatchesById(imdbId, isSeries, stopOnHighConfidence).FirstOrDefault();
        }

        public List<IMediaStream> FindAllMatchesById(string? id, bool isSeries, bool stopOnHighConfidence = false)
        {
            if (string.IsNullOrEmpty(id)) return new List<IMediaStream>();
            
            // Normalize ID: remove common prefixes if present (e.g. imdb_id:tt12345 -> tt12345)
            string normalizedId = id;
            if (id.Contains(":")) normalizedId = id.Split(':').Last();

            lock (_indexLock)
            {
                if (_imdbIndex.TryGetValue(normalizedId, out var list))
                {
                    var matches = list.Where(m => m.Type == (isSeries ? "series" : "movie")).ToList();
                    AppLogger.Info($"[IptvMatch] ID Match HIT for {normalizedId}: Found {matches.Count} results.");
                    if (stopOnHighConfidence && matches.Any()) return new List<IMediaStream> { matches[0] };
                    return matches;
                }
            }
            return new List<IMediaStream>();
        }

        /// <summary>
        /// Globally registers a successful match and persists it for future sessions.
        /// </summary>
        public void RegisterMatch(IMediaStream item, string imdbId)
        {
            if (item == null || string.IsNullOrWhiteSpace(imdbId)) return;

            // 1. Update the item itself
            if (item is VodStream vs) vs.ImdbId = imdbId;
            else if (item is SeriesStream ss) ss.ImdbId = imdbId;
            else return;

            // 2. Update the Runtime Index (Instant availability for current session)
            lock (_indexLock)
            {
                if (!_imdbIndex.ContainsKey(imdbId))
                {
                    _imdbIndex[imdbId] = new List<IMediaStream>();
                }

                var list = _imdbIndex[imdbId];
                if (!list.Contains(item))
                {
                    list.Add(item);
                    AppLogger.Info($"[IptvMatch] Learned: '{item.Title}' -> {imdbId}. Match registered in runtime ID index.");
                }
            }

            // 3. Trigger Persistent Save (Atomic throttled save)
            _ = ContentCacheService.Instance.TriggerThrottledSaveAsync(item is SeriesStream ? "series" : "vod");
        }

        public List<IMediaStream> MatchStremioItemAll(StremioMediaStream item, string? query = null, string? originalTitle = null, string? subTitle = null, string? localizedTitle = null, string? yearOverride = null, bool stopOnHighConfidence = false, System.Threading.CancellationToken ct = default)
        {
            if (item == null) return new List<IMediaStream>();
            ct.ThrowIfCancellationRequested();

            bool isSeries = item.IsSeries;
            string? imdbId = item.IMDbId;
            
            AppLogger.Warn($"[IptvMatch] MatchStremioItemAll for '{item.Title}' (ID: {imdbId}, Type: {(isSeries ? "Series" : "Movie")})");

            var results = new List<IMediaStream>();
            var matchedStreamIds = new HashSet<int>();

            // 1. Direct ID Match (Fastest & Strongest)
            var idMatches = FindAllMatchesById(imdbId, isSeries, stopOnHighConfidence);
            if (idMatches.Any())
            {
                AppLogger.Warn($"[IptvMatch] MatchStremioItemAll: Found {idMatches.Count} matches by ID for '{item.Title}'.");
                foreach (var m in idMatches)
                {
                    results.Add(m);
                    int sid = (m as VodStream)?.StreamId ?? (m as SeriesStream)?.SeriesId ?? 0;
                    if (sid != 0) matchedStreamIds.Add(sid);
                }
            }

            // 2. Supplemental Title Match (Always run to find "all sources")
            AppLogger.Warn($"[IptvMatch] MatchStremioItemAll: Performing supplemental title search for '{item.Title}' to find all available versions.");
            
            // [NEW] Avoid using broad search query if it's just a subset of the item title 
            // (e.g. query "spider man" for item "Spider-Man: Lotus" would cause false positives)
            string? safeQuery = query;
            if (!string.IsNullOrEmpty(query) && !string.IsNullOrEmpty(item.Title))
            {
                var titleTokens = TitleHelper.GetBaseTokens(item.Title);
                var queryTokens = TitleHelper.GetBaseTokens(query);
                if (queryTokens.Count > 0 && queryTokens.Count < titleTokens.Count && queryTokens.All(t => titleTokens.Contains(t)))
                {
                    AppLogger.Warn($"[IptvMatch] MatchStremioItemAll: Ignoring generic search query '{query}' because it is a subset of specific title '{item.Title}'.");
                    safeQuery = null;
                }
            }

            var titleMatches = FindAllMatches(
                item.Title, 
                originalTitle ?? item.Meta?.OriginalName, 
                subTitle, 
                localizedTitle,
                yearOverride ?? item.Year, 
                safeQuery, 
                isSeries,
                stopOnHighConfidence,
                ct
            );

            if (titleMatches.Any())
            {
                int newCount = 0;
                foreach (var m in titleMatches)
                {
                    int sid = (m as VodStream)?.StreamId ?? (m as SeriesStream)?.SeriesId ?? 0;
                    if (sid != 0 && !matchedStreamIds.Contains(sid))
                    {
                        results.Add(m);
                        matchedStreamIds.Add(sid);
                        newCount++;
                    }
                    else if (sid == 0)
                    {
                        // Fallback for types that don't have StreamId/SeriesId (unlikely for VOD/Series)
                        results.Add(m);
                        newCount++;
                    }
                }
                AppLogger.Warn($"[IptvMatch] MatchStremioItemAll: Title search added {newCount} additional unique matches.");
            }

            // [NEW] Learn the match if it was found via Title during search/discovery
            if (results.Any() && !string.IsNullOrEmpty(imdbId) && !idMatches.Any())
            {
                RegisterMatch(results[0], imdbId);
            }

            return results;
        }

        public IMediaStream? MatchStremioItem(StremioMediaStream item, string? query = null, string? originalTitle = null, string? subTitle = null, string? localizedTitle = null, string? yearOverride = null, bool stopOnHighConfidence = false, System.Threading.CancellationToken ct = default)
        {
            return MatchStremioItemAll(item, query, originalTitle, subTitle, localizedTitle, yearOverride, stopOnHighConfidence, ct).FirstOrDefault();
        }
        public List<IMediaStream> SearchFast(string query, string type = "all", System.Threading.CancellationToken ct = default)
        {
            var results = new List<IMediaStream>();
            if (string.IsNullOrEmpty(query)) return results;
            
            string q = query.ToLowerInvariant();
            
            if (type == "all" || type == "movie")
            {
                foreach (var v in _vods)
                {
                    ct.ThrowIfCancellationRequested();
                    if (v.Title != null && v.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                        results.Add(v);
                    
                    if (results.Count > 100) break;
                }
            }

            if (results.Count < 100 && (type == "all" || type == "series"))
            {
                foreach (var s in _series)
                {
                    ct.ThrowIfCancellationRequested();
                    if (s.Title != null && s.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                        results.Add(s);
                    
                    if (results.Count > 150) break;
                }
            }
            return results;
        }
    }
}
