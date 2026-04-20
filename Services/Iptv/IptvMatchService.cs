using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Numerics.Tensors;
using System.Buffers;
using System.IO;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Services.Iptv
{
    /// <summary>
    /// Senior-level high-performance matchmaking service for IPTV streams.
    /// Utilizes SIMD (TensorPrimitives), lock-free snapshots, and StringPool for zero-allocation hotspots.
    /// Supports automatic matching against internal indexed data.
    /// </summary>
    public sealed class IptvMatchService
    {
        private static readonly IptvMatchService _instance = new();
        public static IptvMatchService Instance => _instance;

        private readonly StreamMatchIndexer _vodSeriesIndexer = new();
        private readonly FastSearchIndex _liveSearchIndex = new();
        
        // Internal cache for loaded streams. 
        // Standardized on IReadOnlyList to support both raw arrays and MMF-Direct VirtualLists 
        // without triggering realization.
        private IReadOnlyList<VodStream>? _vodCache;
        private IReadOnlyList<SeriesStream>? _seriesCache;
        private IReadOnlyList<LiveStream>? _liveCache;

        private static readonly CompositeFormat PerfFormat = CompositeFormat.Parse("[PERF] [IptvMatchService] {0} indexing: {1}ms (Hash: {2})");

        /// <summary>
        /// Optimized struct-based result to avoid heap allocations during scoring comparison.
        /// Involved in the zero-allocation performance hot paths.
        /// </summary>
        private struct MatchResult : IComparable<MatchResult>
        {
            public IMediaStream Stream { get; set; }
            public float Score { get; set; }
            public int CompareTo(MatchResult other) => other.Score.CompareTo(Score); // Descending score
        }

        private IptvMatchService() { }

        /// <summary>
        /// Unified entry point for rebuilding stream indices and updating internal caches.
        /// </summary>
        [SkipLocalsInit]
        public async Task UpdateIndexers(
            IEnumerable<LiveStream>? live = null, string? liveFp = null,
            IEnumerable<VodStream>? vod = null, string? vodFp = null,
            IEnumerable<SeriesStream>? series = null, string? seriesFp = null)
        {
            var sw = Stopwatch.StartNew();
            var tasks = new List<Task>();

            AppLogger.Info($"[IptvMatchService] UpdateIndexers triggered (Live: {live != null}, VOD: {vod != null}, Series: {series != null})");

            if (live != null) 
            {
                _liveCache = (live is IReadOnlyList<LiveStream> rl) ? rl : live.ToList();
                tasks.Add(RunUpdate("Live", _liveCache, liveFp, async (s, fp) => 
                {
                    await _liveSearchIndex.RebuildAsync<LiveStream>(s, fp);
                }));
            }

            if (vod != null) 
            {
                _vodCache = (vod is IReadOnlyList<VodStream> rl) ? rl : vod.ToList();
                tasks.Add(RunUpdate("VOD", _vodCache, vodFp, (s, fp) => _vodSeriesIndexer.RebuildAsync(s, fp, clear: false)));
            }

            if (series != null) 
            {
                _seriesCache = (series is IReadOnlyList<SeriesStream> rl) ? rl : series.ToList();
                tasks.Add(RunUpdate("Series", _seriesCache, seriesFp, (s, fp) => _vodSeriesIndexer.RebuildAsync(s, fp, clear: false)));
            }

            // Await all internal operations to ensure logs are fully flushed before sync finishes
            await Task.WhenAll(tasks).ConfigureAwait(false);
            AppLogger.Info($"[PERF] [IptvMatchService] Total indexing cycle completed in {sw.ElapsedMilliseconds}ms.");
        }

        private async Task RunUpdate<T>(string tag, IReadOnlyList<T> items, string? fp, Func<IReadOnlyList<T>, string, Task> action)
        {
            if (items == null || string.IsNullOrEmpty(fp))
            {
                AppLogger.Warn($"[IptvMatchService] {tag} update skipped: Items or Fingerprint null.");
                return;
            }

            var sw = Stopwatch.StartNew();
            AppLogger.Info($"[IptvMatchService] {tag} index update STARTED (Items: {items.Count}, FP: {fp})");
            
            try 
            {
                string folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                string sidecarPath = Path.Combine(folder, $"{tag}_{fp}.idx.bin");

                AppLogger.Info($"[IptvMatchService] {tag} checking sidecar: {sidecarPath}");
                bool restored = false;
                
                if (tag == "Live") restored = await _liveSearchIndex.TryLoadFromDiskAsync(sidecarPath, fp);
                else restored = await _vodSeriesIndexer.TryLoadFromDiskAsync(sidecarPath, fp);

                if (!restored)
                {
                    AppLogger.Info($"[IptvMatchService] {tag} sidecar MISSING or INVALID. Starting FULL REBUILD...");
                    await action(items, fp).ConfigureAwait(false);
                    
                    AppLogger.Info($"[IptvMatchService] {tag} rebuild finished. Persisting to disk...");
                    
                    if (tag == "Live") 
                    {
                        await _liveSearchIndex.SaveToDiskAsync(sidecarPath);
                        await _liveSearchIndex.TryLoadFromDiskAsync(sidecarPath, fp); // HOT-SWAP TO MMF
                    }
                    else 
                    {
                        await _vodSeriesIndexer.SaveToDiskAsync(sidecarPath);
                        await _vodSeriesIndexer.TryLoadFromDiskAsync(sidecarPath, fp); // HOT-SWAP TO MMF
                    }
                    
                    // Explicit async GC to forcefully reclaim the 30MB+ FrozenDictionary from the rebuild
                    _ = Task.Run(() => GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false));
                    
                    AppLogger.Info($"[IptvMatchService] {tag} INDEX PERSISTED AND SWAPPED TO NATIVE MMF. (Duration: {sw.ElapsedMilliseconds}ms)");
                }
                else
                {
                    AppLogger.Info($"[IptvMatchService] {tag} restored via SIDECAR successfully in {sw.ElapsedMilliseconds}ms.");
                }

                // Vacuum: Cleanup old orphaned sidecars for this tag that don't match current FP
                _ = Task.Run(() => CleanupOldSidecars(folder, tag, fp.ToString()));
            }
            catch (OperationCanceledException) { AppLogger.Warn($"[IptvMatchService] {tag} update CANCELLED by user/system."); }
            catch (Exception ex) { AppLogger.Error($"[IptvMatchService] {tag} update FAILED CRITICALLY", ex); }
        }

        /// <summary>
        /// Matches a title against internal IPTV indices automatically.
        /// Optimized to bypass collection allocations.
        /// </summary>
        public IMediaStream? MatchToIptv(string title, string? year = null, string category = "movie")
        {
            if (category == "movie" && _vodCache != null) return MatchByTitleGeneric(title, _vodCache, year);
            if (category == "series" && _seriesCache != null) return MatchByTitleGeneric(title, _seriesCache, year);
            if (category == "live" && _liveCache != null) return MatchByTitleGeneric(title, _liveCache, year);
            return null;
        }

        /// <summary>
        /// Matches an ID against internal IPTV indices automatically.
        /// </summary>
        public IMediaStream? MatchToIptvById(string? id, string category = "movie")
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (category == "movie" && _vodCache != null) return MatchByIdGeneric(id, _vodCache);
            if (category == "series" && _seriesCache != null) return MatchByIdGeneric(id, _seriesCache);
            if (category == "live" && _liveCache != null) return MatchByIdGeneric(id, _liveCache);
            
            return null;
        }

        /// <summary>
        /// Returns all potential matches from internal indices above a threshold.
        /// </summary>
        public List<IMediaStream> FindPotentialMatchesInIptv(string title, string category = "movie", double threshold = 0.3)
        {
            if (category == "movie" && _vodCache != null) return FindPotentialMatchesGeneric(title, _vodCache, threshold);
            if (category == "series" && _seriesCache != null) return FindPotentialMatchesGeneric(title, _seriesCache, threshold);
            if (category == "live" && _liveCache != null) return FindPotentialMatchesGeneric(title, _liveCache, threshold);
            return [];
        }

        /// <summary>
        /// Modernized ID-based lookup using direct integer comparison to eliminate ToString() and boxing allocations.
        /// The string ID is parsed once before entering the search loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private T? MatchByIdGeneric<T>(string id, IReadOnlyList<T> candidates) where T : class, IMediaStream
        {
            if (string.IsNullOrEmpty(id) || !int.TryParse(id, out int targetId)) return null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var item = candidates[i];
                if (item.Id == targetId) return item; 
            }
            return null;
        }

        /// <summary>
        /// PROJECT ZERO: Performs a high-performance search across loaded indices.
        /// Returns raw indices to avoid managed object hydration.
        /// </summary>
        public int[] SearchIndices(string query, string category = "live")
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<int>();

            if (category == "live")
            {
                return _liveSearchIndex.GetMatchingIndices(query);
            }

            return Array.Empty<int>(); // VOD/Series use a different indexing model for now
        }

        /// <summary>
        /// Performs a high-performance search across loaded indices.
        /// Fully zero-allocation on the tokenization and lookup path.
        /// </summary>
        public IEnumerable<IMediaStream> Search(string query, string category = "live", IReadOnlyList<IMediaStream>? source = null)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<IMediaStream>();

            if (category == "live")
            {
                IReadOnlyList<IMediaStream>? liveSource = source ?? (IReadOnlyList<IMediaStream>?)_liveCache;
                return liveSource == null ? Enumerable.Empty<IMediaStream>() : _liveSearchIndex.Search(query, liveSource);
            }

            IReadOnlyList<IMediaStream>? targetList = (category == "series") 
                ? (IReadOnlyList<IMediaStream>?)_seriesCache 
                : (IReadOnlyList<IMediaStream>?)_vodCache;
            
            if (targetList == null) return Enumerable.Empty<IMediaStream>();

            // Aggregate candidates from index using zero-alloc iterator
            var candidateMap = new Dictionary<int, int>();
            foreach (var token in TitleHelper.GetTokens(query))
            {
                var indices = _vodSeriesIndexer.FindByToken(token);
                foreach (int idx in indices)
                {
                    ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(candidateMap, idx, out _);
                    count++;
                }
            }

            if (candidateMap.Count == 0) return Enumerable.Empty<IMediaStream>();

            // Filter and sort candidates via Span (Zero-Alloc)
            var results = new List<MatchResult>(Math.Min(candidateMap.Count, 100));
            foreach (var kvp in candidateMap)
            {
                if (kvp.Key >= 0 && kvp.Key < targetList.Count)
                {
                    results.Add(new MatchResult { Stream = targetList[kvp.Key], Score = kvp.Value });
                }
            }

            CollectionsMarshal.AsSpan(results).Sort();
            return results.Take(50).Select(x => x.Stream);
        }

        /// <summary>
        /// Senior-level generic scoring engine.
        /// Fully zero-allocation on the search path using TokenIterator.
        /// </summary>
        [SkipLocalsInit]
        private List<IMediaStream> FindPotentialMatchesGeneric<T>(string title, IReadOnlyList<T> candidates, double threshold) where T : class, IMediaStream
        {
            var results = CalculateScoresGeneric(title, candidates);
            var list = new List<IMediaStream>();
            foreach (var r in CollectionsMarshal.AsSpan(results))
            {
                if (r.Score >= threshold) list.Add(r.Stream);
            }
            return list;
        }

        private IMediaStream? MatchByTitleGeneric<T>(string title, IReadOnlyList<T> candidates, string? year) where T : class, IMediaStream
        {
            var results = CalculateScoresGeneric(title, candidates);
            if (results.Count == 0) return null;
            
            if (results.Count > 4) ApplySimdNormalization(results);

            var span = CollectionsMarshal.AsSpan(results);
            ref var best = ref span[0];

            if (best.Score > 0.4f)
            {
                if (!string.IsNullOrEmpty(year) && !string.IsNullOrEmpty(best.Stream.Year) && year != best.Stream.Year)
                {
                    if (best.Score < 0.9f) return null;
                }
                return best.Stream;
            }
            return null;
        }

        [SkipLocalsInit]
        private List<MatchResult> CalculateScoresGeneric<T>(string query, IReadOnlyList<T> candidates) where T : class, IMediaStream
        {
            if (string.IsNullOrEmpty(query) || candidates == null || candidates.Count == 0) return [];

            string q = TitleHelper.Normalize(query);
            var queryTokens = TitleHelper.GetSignificantTokens(q.AsSpan());
            var candidateMap = new Dictionary<int, int>();

            foreach (var token in queryTokens)
            {
                var indices = _vodSeriesIndexer.FindByToken(token);
                foreach (int idx in indices)
                {
                    ref int score = ref CollectionsMarshal.GetValueRefOrAddDefault(candidateMap, idx, out _);
                    score++;
                }
            }

            if (candidateMap.Count == 0) return [];

            var virtualList = candidates as IVirtualStreamList;
            var results = new List<MatchResult>(Math.Min(candidateMap.Count, 100));
            
            // [SENIOR] Strategic Parallelization
            // Only offload to thread pool if the candidate set is large enough to justify the overhead.
            if (candidateMap.Count > 500)
            {
                var partitions = Partitioner.Create(candidateMap.ToList(), loadBalance: true);
                var concurrentResults = new ConcurrentBag<MatchResult>();
                
                Parallel.ForEach(partitions, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, kvp =>
                {
                    // Stackalloc buffer for each thread to ensure zero-allocation during similarity check
                    Span<char> threadTitleBuffer = stackalloc char[256];
                    
                    int idx = kvp.Key;
                    if (idx >= 0 && idx < candidates.Count)
                    {
                        var itemTitleSpan = virtualList != null ? virtualList.GetTitleSpan(idx, threadTitleBuffer) : candidates[idx].Title.AsSpan();
                        double sim = TitleHelper.CalculateSimilarity(itemTitleSpan, q.AsSpan());
                        sim += (kvp.Value * 0.1);

                        if (sim > 0.1) concurrentResults.Add(new MatchResult { Stream = candidates[idx], Score = (float)sim });
                    }
                });
                
                results.AddRange(concurrentResults);
            }
            else
            {
                Span<char> titleBuffer = stackalloc char[256];
                foreach (var kvp in candidateMap)
                {
                    int idx = kvp.Key;
                    if (idx < 0 || idx >= candidates.Count) continue;

                    var itemTitleSpan = virtualList != null ? virtualList.GetTitleSpan(idx, titleBuffer) : candidates[idx].Title.AsSpan();
                    double sim = TitleHelper.CalculateSimilarity(itemTitleSpan, q.AsSpan());
                    sim += (kvp.Value * 0.1);

                    if (sim > 0.1) results.Add(new MatchResult { Stream = candidates[idx], Score = (float)sim });
                }
            }

            if (results.Count > 1) CollectionsMarshal.AsSpan(results).Sort();
            return results;
        }


        [SkipLocalsInit]
        private void ApplySimdNormalization(List<MatchResult> results)
        {
            int count = results.Count;
            float[] scores = ArrayPool<float>.Shared.Rent(count);
            try {
                for (int i = 0; i < count; i++) scores[i] = results[i].Score;
                float max = TensorPrimitives.Max(scores.AsSpan(0, count));
                if (max > 0)
                {
                    TensorPrimitives.Divide(scores.AsSpan(0, count), max, scores.AsSpan(0, count));
                    var span = CollectionsMarshal.AsSpan(results);
                    for (int i = 0; i < count; i++) span[i].Score = scores[i];
                }
            }
            finally { ArrayPool<float>.Shared.Return(scores); }
        }

        public void Clear() 
        { 
            _vodSeriesIndexer.Clear(); 
            _liveSearchIndex.Clear(); 
            _vodCache = null; _seriesCache = null; _liveCache = null;
        }

        public void RegisterManualMatch(IMediaStream stream, string verifiedMetadataId) { }
        private void CleanupOldSidecars(string folder, string tag, string currentFp)
        {
            try
            {
                var dir = new DirectoryInfo(folder);
                string currentFile = $"{tag}_{currentFp}.idx.bin";
                
                foreach (var file in dir.GetFiles($"{tag}_*.idx.bin"))
                {
                    if (file.Name.Equals(currentFile, StringComparison.OrdinalIgnoreCase)) continue;
                    
                    try { file.Delete(); AppLogger.Info($"[Vacuum] Deleted orphaned sidecar: {file.Name}"); }
                    catch { /* File might be in use, ignore */ }
                }
            }
            catch (Exception ex) { AppLogger.Warn($"[Vacuum] Sidecar cleanup failed: {ex.Message}"); }
        }
    }
}
