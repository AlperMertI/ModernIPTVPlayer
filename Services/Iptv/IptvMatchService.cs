using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Services.Iptv
{
    public class IptvMatchService
    {
        private static IptvMatchService _instance;
        public static IptvMatchService Instance => _instance ??= new IptvMatchService();

        private IReadOnlyList<IMediaStream> _vods = Array.Empty<IMediaStream>();
        private IReadOnlyList<IMediaStream> _series = Array.Empty<IMediaStream>();
        private Dictionary<string, int[]> _imdbIndex = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int[]> _tokenIndex = new(StringComparer.OrdinalIgnoreCase);
        private bool _isInitialized = false;
        private readonly object _indexLock = new();

        private IptvMatchService() { }

        /// <summary>
        /// Updates the internal stream lists and indices atomically.
        /// indices are pre-built by ContentCacheService in a background thread.
        /// </summary>
        public void UpdateIndices(IReadOnlyList<IMediaStream> vods, IReadOnlyList<IMediaStream> series, 
            Dictionary<string, int[]> imdbIndex, Dictionary<string, int[]> tokenIndex, string? correlationId = null, string? source = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            correlationId ??= LifecycleLog.NewId("matchidx");
            using var lifecycle = LifecycleLog.Begin("Match.Index.Publish", correlationId, new Dictionary<string, object?>
            {
                ["source"] = source ?? "unknown"
            });

            lock (_indexLock)
            {
                _vods = vods ?? Array.Empty<IMediaStream>();
                _series = series ?? Array.Empty<IMediaStream>();
                
                _imdbIndex = imdbIndex ?? new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                _tokenIndex = tokenIndex ?? new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
                
                _isInitialized = true;
                TitleHelper.ClearCaches();
                lifecycle.Step("published", new Dictionary<string, object?>
                {
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["vodCount"] = _vods.Count,
                    ["seriesCount"] = _series.Count,
                    ["imdbKeys"] = _imdbIndex.Count,
                    ["tokenKeys"] = _tokenIndex.Count
                });
            }
        }

        // Backward compatibility
        public void Initialize(IReadOnlyList<IMediaStream> vods, IReadOnlyList<IMediaStream> series)
        {
             // This is now handled more efficiently via UpdateIndices from ContentCacheService.
             // But if called directly, we'll do a basic build.
             var imdbIndex = new Dictionary<string, List<IMediaStream>>(StringComparer.OrdinalIgnoreCase);
             foreach (var item in (vods ?? Enumerable.Empty<IMediaStream>()).Concat(series ?? Enumerable.Empty<IMediaStream>()))
             {
                 if (string.IsNullOrEmpty(item.IMDbId)) continue;
                 
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

             var vodList = vods?.ToList() ?? new List<IMediaStream>();
             var seriesList = series?.ToList() ?? new List<IMediaStream>();

             var imdbRun = imdbIndex.ToDictionary(
                 k => k.Key, 
                 v => v.Value.Select(s => {
                     if (s is SeriesStream ss) {
                         int idx = seriesList.IndexOf(ss);
                         return idx >= 0 ? -(idx + 1) : 0;
                     } else {
                         int idx = vodList.IndexOf(s);
                         return idx >= 0 ? idx : 0;
                     }
                 }).ToArray(), 
                 StringComparer.OrdinalIgnoreCase);

             var tokenRun = tokenIndex.ToDictionary(
                 k => k.Key, 
                 v => v.Value.Select(s => {
                     if (s is SeriesStream ss) {
                         int idx = seriesList.IndexOf(ss);
                         return idx >= 0 ? -(idx + 1) : 0;
                     } else {
                         int idx = vodList.IndexOf(s);
                         return idx >= 0 ? idx : 0;
                     }
                 }).ToArray(), 
                 StringComparer.OrdinalIgnoreCase);

             UpdateIndices(vods, series, imdbRun, tokenRun);
        }

        public bool InstanceInitializeCheck() => _isInitialized;

        public IMediaStream? FindMatch(string? title, string? originalTitle, string? subTitle, string? localizedTitle, string? year, string? query, bool isSeries, System.Threading.CancellationToken ct = default)
        {
            return FindAllMatches(title, originalTitle, subTitle, localizedTitle, year, query, isSeries, false, false, ct).FirstOrDefault();
        }

        public List<IMediaStream> FindAllMatches(string? title, string? originalTitle, string? subTitle, string? localizedTitle, string? year, string? query, bool isSeries, bool stopOnHighConfidence = false, bool isLearning = false, System.Threading.CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) {
                System.Diagnostics.Debug.WriteLine($"[IptvMatch] CANCELLED before start for Query: {query ?? title}");
                ct.ThrowIfCancellationRequested();
            }

            var searchTitles = new[] { title, originalTitle, subTitle, localizedTitle, query }
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
            
            if (!searchTitles.Any()) return new List<IMediaStream>();

            string searchType = isSeries ? "SERIES" : "VOD";
            if (!_isInitialized)
            {
                if (LifecycleLog.ShouldLog($"iptv-match-notready-{searchType}", TimeSpan.FromSeconds(20)))
                {
                    AppLogger.Info($"[Lifecycle] ➔ Match.Query [SKIPPED_NOT_READY] ({query ?? title}) | type={searchType}, year={year ?? "null"}, tokens={_tokenIndex.Count}, vods={_vods.Count}, series={_series.Count}");
                }
                return new List<IMediaStream>();
            }

            if (LifecycleLog.ShouldLog($"iptv-match-start-{searchType}", TimeSpan.FromSeconds(10)))
            {
                AppLogger.Info($"[Lifecycle] ◎ Match.Query [START] ({query ?? title}) | type={searchType}, year={year ?? "null"}, titleVariants={searchTitles.Count}, learning={isLearning}");
            }

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

                    // [PROJECT ZERO] Use our runtime index directly if initialized.
                    // This avoids the expensive and potentially crashing FindMatchesBinaryAsync call.
                    if (_isInitialized && _tokenIndex.TryGetValue(token, out var matches))
                    {
                        foreach (var mIdx in matches)
                        {
                            IMediaStream? m = null;
                            lock(_indexLock)
                            {
                                if (mIdx < 0) // Series
                                {
                                    int realIdx = -mIdx - 1;
                                    if (isSeries && realIdx >= 0 && realIdx < _series.Count) m = _series[realIdx];
                                }
                                else // VOD
                                {
                                    if (!isSeries && mIdx >= 0 && mIdx < _vods.Count) m = _vods[mIdx];
                                }
                            }
    
                            if (m == null) continue;
    
                            ct.ThrowIfCancellationRequested();
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
            // [STRICT] DISABLED during learning phase to prevent series matching movies.
            if (!candidates.Any() && !isLearning)
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
                            foreach (var mIdx in matches)
                            {
                                IMediaStream? m = null;
                                if (mIdx < 0) // Series
                                {
                                    int realIdx = -mIdx - 1;
                                    if (realIdx >= 0 && realIdx < _series.Count) m = _series[realIdx];
                                }
                                else // VOD
                                {
                                    if (mIdx >= 0 && mIdx < _vods.Count) m = _vods[mIdx];
                                }

                                if (m == null || m.Type != fallbackType) continue;
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
                if (LifecycleLog.ShouldLog($"iptv-match-summary-empty-{searchType}", TimeSpan.FromSeconds(10)))
                {
                    AppLogger.Info($"[Lifecycle] ✓ Match.Query [DONE] ({query ?? title}) | type={searchType}, candidates=0, results=0");
                }
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
                if (_imdbIndex.TryGetValue(normalizedId, out var matches))
                {
                    var results = new List<IMediaStream>();
                    foreach (var mIdx in matches)
                    {
                        IMediaStream? m = null;
                        if (mIdx < 0) // Series
                        {
                            int realIdx = -mIdx - 1;
                            if (isSeries && realIdx >= 0 && realIdx < _series.Count) m = _series[realIdx];
                        }
                        else // VOD
                        {
                            if (!isSeries && mIdx >= 0 && mIdx < _vods.Count) m = _vods[mIdx];
                        }

                        if (m != null) results.Add(m);
                    }
                    AppLogger.Info($"[IptvMatch] ID Match HIT for {normalizedId}: Found {results.Count} results.");
                    if (stopOnHighConfidence && results.Any()) return new List<IMediaStream> { results[0] };
                    return results;
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
                    _imdbIndex[imdbId] = Array.Empty<int>();
                }

                var arr = _imdbIndex[imdbId];
                int packedIdx = -1;
                
                if (item is SeriesStream seriesItem) {
                    if (seriesItem.RecordIndex >= 0) packedIdx = -(seriesItem.RecordIndex + 1);
                } else if (item is VodStream vodItem) {
                    if (vodItem.RecordIndex >= 0) packedIdx = vodItem.RecordIndex;
                }

                if (packedIdx != -1 && !arr.Contains(packedIdx))
                {
                    var newArr = new int[arr.Length + 1];
                    Array.Copy(arr, newArr, arr.Length);
                    newArr[arr.Length] = packedIdx;
                    _imdbIndex[imdbId] = newArr;
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
                true, // isLearning = true
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

            // [NEW] Learn the matches if they were found via Title during search/discovery
            if (results.Any() && !string.IsNullOrEmpty(imdbId))
            {
                int learnCount = 0;
                foreach (var m in results)
                {
                    // Check if this specific stream already has this IMDB ID linked in runtime index
                    string currentId = (m as VodStream)?.ImdbId ?? (m as SeriesStream)?.ImdbId;
                    if (currentId != imdbId)
                    {
                        RegisterMatch(m, imdbId);
                        learnCount++;
                    }
                }
                if (learnCount > 0)
                {
                    AppLogger.Info($"[Lifecycle] ➔ Match.Query [LEARNED] ({item.Title}) | imdbId={imdbId}, newLinks={learnCount}");
                }
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
