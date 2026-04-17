using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Services.Iptv; // NEW
using Windows.Storage;
using ModernIPTVPlayer;
using System.Runtime.InteropServices;

namespace ModernIPTVPlayer.Services
{
    public class ContentCacheService
    {
        private static ContentCacheService _instance;
        public static ContentCacheService Instance => _instance ??= new ContentCacheService();

        public static long ThrottledSaveAttempts;
        public static long ThrottledSaveFailures;




        private const string HASH_EXT = ".hash";
        private bool _isSyncing = false;
        
        // In-Memory Search Index (Simple Dictionary for now, can be Trie later)
        private ConcurrentDictionary<string, List<object>> _searchIndex = new();

        // **NEW: RAM Cache Layer for Series Info**
        // Key: cacheKey, Value: (Data, Expiration)
        private ConcurrentDictionary<string, (object Data, DateTime LastAccessed)> _memoryCache = new();

        // **NEW: RAM Cache for Full Stream Lists (VOD/Series/Live)**
        private ConcurrentDictionary<string, object> _streamListsCache = new();

        // **NEW: RAM Cache for Live Streams (instant re-navigation)**
        // Key: playlistId, Value: (Streams, Categories, Timestamp)
        private ConcurrentDictionary<string, (IReadOnlyList<LiveStream> Streams, IReadOnlyList<LiveCategory> Categories, DateTime Timestamp)> _liveRamCache = new();
        private const int RAM_CACHE_TTL_MINUTES = 60; // Invalidate after 60 minutes

        private CancellationTokenSource _syncCts;
        private readonly StreamMatchIndexer _vodMatchIndex = new();
        private readonly StreamMatchIndexer _seriesMatchIndex = new();

        private ConcurrentDictionary<string, byte> _pendingSaveCategories = new();
        private Timer _throttledSaveTimer;

        private bool _isIndexing = false;
        public bool IsIndexing 
        { 
            get => _isIndexing; 
            private set { 
                if (_isIndexing != value) { 
                    _isIndexing = value; 
                    // Safe Invoke: If this belongs to UI, Marshall it.
                    // ContentCacheService is a singleton often used by UI.
                    if (App.MainWindow.DispatcherQueue != null)
                    {
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            IndexingStatusChanged?.Invoke(this, value);
                        });
                    }
                    else
                    {
                        IndexingStatusChanged?.Invoke(this, value);
                    }
                } 
            } 
        }
        public event EventHandler<bool> IndexingStatusChanged;
        
        private readonly SemaphoreSlim _diskSemaphore = new(1, 1);
        private readonly SemaphoreSlim _indexRefreshSemaphore = new(1, 1);

        /// <summary>
        /// PROJECT ZERO: Surgical In-Place Patching.
        /// Finds all instances of a movie (resolutions/languages) and updates their metadata on disk.
        /// </summary>
        public async Task HydrateInPlaceAsync(string playlistId, string imdbId, Models.Metadata.UnifiedMetadata enriched, bool isSeries)
        {
            if (string.IsNullOrEmpty(imdbId)) return;
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_" + (isSeries ? "series.bin" : "vod.bin");

            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return;

                var file = await folder.GetFileAsync(fileName);
                
                // Surgical Patching Fix: Use single BinaryCacheSession
                int count, version, bufferLen;
                using (var mmf = BinaryCacheSession.OpenMemoryMappedFile(file.Path, MemoryMappedFileAccess.Read))
                using (var accessor = mmf.CreateViewAccessor(0, 32, MemoryMappedFileAccess.Read))
                {
                    version = accessor.ReadInt32(4);
                    count = accessor.ReadInt32(8);
                    bufferLen = accessor.ReadInt32(12);
                }

                if (version < 2) 
                {
                    AppLogger.Warn($"[InPlaceHydration] Skipping legacy Version {version} cache. Needs rebuild.");
                    return;
                }

                int recordSize = isSeries ? Marshal.SizeOf<Models.Metadata.SeriesRecord>() : Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = 32;
                long indexOffset = recordsOffset + (count * (long)recordSize);
                long stringsOffset = isSeries ? indexOffset : indexOffset + (count * 8L);

                using var session = new BinaryCacheSession(file.Path, stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: false);

                // [PROJECT ZERO] Shared Pointer Strategy: Prepare offsets ONCE for all matching records
                (int Off, int Len) sharedImdb = !string.IsNullOrEmpty(imdbId) ? session.AppendString(imdbId) : (-1, 0);
                (int Off, int Len) sharedTitle = !string.IsNullOrEmpty(enriched.Title) ? session.AppendString(enriched.Title) : (-1, 0);
                (int Off, int Len) sharedPlot = !string.IsNullOrEmpty(enriched.Overview) ? session.AppendString(enriched.Overview) : (-1, 0);
                (int Off, int Len) sharedIcon = !string.IsNullOrEmpty(enriched.PosterUrl) ? session.AppendString(enriched.PosterUrl) : (-1, 0);
                
                // Multi-Backdrop Support (Pipe-delimited)
                string backdropStr = enriched.BackdropUrl;
                if (enriched.BackdropUrls?.Count > 1) backdropStr = string.Join("|", enriched.BackdropUrls);
                (int Off, int Len) sharedBg = !string.IsNullOrEmpty(backdropStr) ? session.AppendString(backdropStr) : (-1, 0);

                (int Off, int Len) sharedGenre = !string.IsNullOrEmpty(enriched.Genres) ? session.AppendString(enriched.Genres) : (-1, 0);
                (int Off, int Len) sharedYear = !string.IsNullOrEmpty(enriched.Year) ? session.AppendString(enriched.Year) : (-1, 0);
                
                string castStr = enriched.Cast != null ? string.Join(", ", enriched.Cast.Select(c => c.Name)) : "";
                (int Off, int Len) sharedCast = !string.IsNullOrEmpty(castStr) ? session.AppendString(castStr) : (-1, 0);
                
                string dirStr = enriched.Directors != null ? string.Join(", ", enriched.Directors.Select(d => d.Name)) : "";
                (int Off, int Len) sharedDir = !string.IsNullOrEmpty(dirStr) ? session.AppendString(dirStr) : (-1, 0);

                (int Off, int Len) sharedTrailer = !string.IsNullOrEmpty(enriched.TrailerUrl) ? session.AppendString(enriched.TrailerUrl) : (-1, 0);
                (int Off, int Len) sharedRatingStr = enriched.Rating > 0 ? session.AppendString(enriched.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)) : (-1, 0);

                int updateCount = 0;
                uint incomingFingerprint = CalculateFingerprint(enriched.Title, enriched.Year);

                unsafe
                {
                    for (int i = 0; i < count; i++)
                    {
                        bool isMatch = false;
                        if (isSeries)
                        {
                            var recordPtr = session.GetRecordPointer<Models.Metadata.SeriesRecord>(i);
                            string currentImdb = session.GetString(recordPtr->ImdbIdOff, recordPtr->ImdbIdLen);
                            
                            if (currentImdb == imdbId && !string.IsNullOrEmpty(imdbId)) isMatch = true;
                            else if (recordPtr->Fingerprint == incomingFingerprint) isMatch = true;

                            if (isMatch)
                            {
                                recordPtr->PriorityScore = enriched.PriorityScore;
                                recordPtr->LastModified = DateTime.UtcNow.Ticks;
                                
                                // Source Title Logic: Preserve original IPTV name
                                if (recordPtr->SourceTitleOff < 0 || recordPtr->SourceTitleLen == 0)
                                {
                                    recordPtr->SourceTitleOff = recordPtr->NameOff;
                                    recordPtr->SourceTitleLen = recordPtr->NameLen;
                                }

                                // Update String Pointers
                                if (sharedTitle.Off >= 0) { recordPtr->NameOff = sharedTitle.Off; recordPtr->NameLen = sharedTitle.Len; }
                                if (sharedImdb.Off >= 0) { recordPtr->ImdbIdOff = sharedImdb.Off; recordPtr->ImdbIdLen = sharedImdb.Len; }
                                if (sharedPlot.Off >= 0) { recordPtr->PlotOff = sharedPlot.Off; recordPtr->PlotLen = sharedPlot.Len; }
                                if (sharedIcon.Off >= 0) { recordPtr->IconOff = sharedIcon.Off; recordPtr->IconLen = sharedIcon.Len; }
                                if (sharedYear.Off >= 0) { recordPtr->YearOff = sharedYear.Off; recordPtr->YearLen = sharedYear.Len; }
                                if (sharedGenre.Off >= 0) { recordPtr->GenresOff = sharedGenre.Off; recordPtr->GenresLen = sharedGenre.Len; }
                                if (sharedCast.Off >= 0) { recordPtr->CastOff = sharedCast.Off; recordPtr->CastLen = sharedCast.Len; }
                                if (sharedDir.Off >= 0) { recordPtr->DirectorOff = sharedDir.Off; recordPtr->DirectorLen = sharedDir.Len; }
                                if (sharedTrailer.Off >= 0) { recordPtr->TrailerOff = sharedTrailer.Off; recordPtr->TrailerLen = sharedTrailer.Len; }
                                if (sharedBg.Off >= 0) { recordPtr->BackdropOff = sharedBg.Off; recordPtr->BackdropLen = sharedBg.Len; }
                                if (sharedRatingStr.Off >= 0) { recordPtr->RatingOff = sharedRatingStr.Off; recordPtr->RatingLen = sharedRatingStr.Len; }
                                
                                recordPtr->RatingScaled = (short)(enriched.Rating * 100);
                                updateCount++;
                            }
                        }
                        else
                        {
                            var recordPtr = session.GetRecordPointer<Models.Metadata.VodRecord>(i);
                            string currentImdb = session.GetString(recordPtr->ImdbIdOff, recordPtr->ImdbIdLen);
                            
                            isMatch = (currentImdb == imdbId && !string.IsNullOrEmpty(imdbId));
                            if (!isMatch)
                            {
                                if (recordPtr->Fingerprint == incomingFingerprint) isMatch = true;
                            }

                            if (isMatch)
                            {
                                recordPtr->PriorityScore = enriched.PriorityScore;
                                recordPtr->LastModified = DateTime.UtcNow.Ticks;

                                // Source Title Logic
                                if (recordPtr->SourceTitleOff < 0 || recordPtr->SourceTitleLen == 0)
                                {
                                    recordPtr->SourceTitleOff = recordPtr->NameOff;
                                    recordPtr->SourceTitleLen = recordPtr->NameLen;
                                }
                                
                                // Update String Pointers
                                if (sharedTitle.Off >= 0) { recordPtr->NameOff = sharedTitle.Off; recordPtr->NameLen = sharedTitle.Len; }
                                if (sharedImdb.Off >= 0) { recordPtr->ImdbIdOff = sharedImdb.Off; recordPtr->ImdbIdLen = sharedImdb.Len; }
                                if (sharedPlot.Off >= 0) { recordPtr->PlotOff = sharedPlot.Off; recordPtr->PlotLen = sharedPlot.Len; }
                                if (sharedIcon.Off >= 0) { recordPtr->IconOff = sharedIcon.Off; recordPtr->IconLen = sharedIcon.Len; }
                                if (sharedYear.Off >= 0) { recordPtr->YearOff = sharedYear.Off; recordPtr->YearLen = sharedYear.Len; }
                                if (sharedGenre.Off >= 0) { recordPtr->GenresOff = sharedGenre.Off; recordPtr->GenresLen = sharedGenre.Len; }
                                if (sharedCast.Off >= 0) { recordPtr->CastOff = sharedCast.Off; recordPtr->CastLen = sharedCast.Len; }
                                if (sharedDir.Off >= 0) { recordPtr->DirectorOff = sharedDir.Off; recordPtr->DirectorLen = sharedDir.Len; }
                                if (sharedTrailer.Off >= 0) { recordPtr->TrailerOff = sharedTrailer.Off; recordPtr->TrailerLen = sharedTrailer.Len; }
                                if (sharedBg.Off >= 0) { recordPtr->BackdropOff = sharedBg.Off; recordPtr->BackdropLen = sharedBg.Len; }
                                if (sharedRatingStr.Off >= 0) { recordPtr->RatingOff = sharedRatingStr.Off; recordPtr->RatingLen = sharedRatingStr.Len; }

                                recordPtr->RatingScaled = (short)(enriched.Rating * 100);
                                updateCount++;
                            }
                        }
                    }
                }



                if (updateCount > 0)
                {
                    // [PERSISTENCE FIX] Update the string buffer length in the header
                    // This is the "Root Fix" for enriched data not persisting across sessions.
                    session.UpdateHeaderStringsLen((int)session.HeapTail - (int)session.StringBufferOffset);
                    AppLogger.Info($"[InPlaceHydration] Patched {updateCount} record(s) for {enriched.Title} ({imdbId})");
                }
            }
            catch (IOException ioEx)
            {
                AppLogger.Error($"[InPlaceHydration] IO FAILED ({fileName}): {ioEx.Message}. If another memory-mapped view holds this file, ensure all opens use FileShare.ReadWrite.", ioEx);
            }
            catch (Exception ex) { AppLogger.Error($"[InPlaceHydration] FAILED ({fileName})", ex); }
            finally { _diskSemaphore.Release(); }
        }

        public static uint CalculateFingerprint(string title, string year, string? imdbId = null)
        {
            if (string.IsNullOrEmpty(title)) return 0;
            uint hash = 2166136261;
            foreach (char c in title) hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;
            if (!string.IsNullOrEmpty(year))
                foreach (char c in year) hash = (hash ^ c) * 16777619;
            if (!string.IsNullOrEmpty(imdbId))
                foreach (char c in imdbId) hash = (hash ^ c) * 16777619;
            return hash;
        }

        /// <summary>
        /// PERFORMANCE FIX: Multi-threaded parallel fingerprint calculation for the entire dataset.
        /// Reduces 3.5s UI hang to ~50ms on 8-core CPUs.
        /// </summary>
         public static long CalculateDatasetFingerprintParallel<T>(IEnumerable<T> items) where T : Models.IMediaStream
         {
             if (items == null) return 0;
             var list = (items as IReadOnlyList<T>) ?? items.ToList();
             if (list.Count == 0) return 0;

             long combinedHash = 0;
             object sync = new object();
             
             Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, list.Count), 
                 () => 0L,
                 (range, state, local) => 
                 {
                     for (int i = range.Item1; i < range.Item2; i++)
                     {
                         var item = list[i];
                         uint f = CalculateFingerprint(item.Title, item.Year, item.IMDbId);
                         local ^= ((long)f << 32) | (uint)(i % 0xFFFFFFFF); 
                     }
                     return local;
                 },
                 (local) => 
                 {
                     lock (sync) combinedHash ^= local;
                 }
             );

             return combinedHash ^ (long)list.Count;
         }

        private static bool TitleContainsBaseToken(string title, string token)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(token)) return false;
            foreach (var t in TitleHelper.GetBaseTokens(title))
            {
                if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public async Task<List<int>> FindMatchesBinaryAsync(string playlistId, string token, bool isSeries)
        {
            var results = new List<int>();
            if (string.IsNullOrWhiteSpace(token)) return results;

            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_" + (isSeries ? "series.bin" : "vod.bin");

            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return results;

                var file = await folder.GetFileAsync(fileName);
                int count, bufferLen, version;
                using (var mmf = BinaryCacheSession.OpenMemoryMappedFile(file.Path, MemoryMappedFileAccess.Read))
                using (var acc = mmf.CreateViewAccessor(0, 32, MemoryMappedFileAccess.Read))
                {
                    int magic = acc.ReadInt32(0);
                    if (magic != (isSeries ? 0x53455244 : 0x564F4444)) return results;
                    version = acc.ReadInt32(4);
                    if (version < 2) return results;
                    count = acc.ReadInt32(8);
                    bufferLen = acc.ReadInt32(12);
                }

                if (count <= 0) return results;

                int recordSize = isSeries ? Marshal.SizeOf<Models.Metadata.SeriesRecord>() : Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = 32;
                long stringsOffset = isSeries
                    ? recordsOffset + (count * (long)recordSize)
                    : recordsOffset + (count * (long)recordSize) + (count * 8L);

                using var session = new BinaryCacheSession(file.Path, stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                unsafe
                {
                    if (isSeries)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var p = session.GetRecordPointer<Models.Metadata.SeriesRecord>(i);
                            if (p == null) continue;
                            string name = session.GetString(p->NameOff, p->NameLen);
                            if (TitleContainsBaseToken(name, token)) results.Add(i);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var p = session.GetRecordPointer<Models.Metadata.VodRecord>(i);
                            if (p == null) continue;
                            string name = session.GetString(p->NameOff, p->NameLen);
                            if (TitleContainsBaseToken(name, token)) results.Add(i);
                        }
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[IptvMatchIndex] Scan failed for {(isSeries ? "series" : "vod")}: {ex.Message}");
                return results;
            }
            finally { _diskSemaphore.Release(); }
        }

        private ContentCacheService()
        {
            AppSettings.CacheSettingsChanged += () => _ = ScheduleNextSyncAsync();
            App.LoginChanged += (login) => 
            {
                if (login != null)
                {
                    AppLogger.Info($"[ContentCache] Login detected for {login.PlaylistName}. Triggering background sync.");
                    _ = SyncPlaylistAsync(login);
                    _ = RefreshIptvMatchIndexAsync();
                }
            };

            // Handle current login if already set during initialization
            if (App.CurrentLogin != null)
            {
                AppLogger.Info($"[ContentCache] Current login already set for {App.CurrentLogin.PlaylistName}. Triggering initial sync.");
                _ = SyncPlaylistAsync(App.CurrentLogin);
            }

            _throttledSaveTimer = new Timer(async _ => await ProcessThrottledSavesAsync(), null, -1, -1);
            _ = StartBackgroundSyncAsync();
        }

        private async Task StartBackgroundSyncAsync()
        {
            // Initial Index Build (from disk cache)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000);

                    System.Diagnostics.Debug.WriteLine("[BackgroundSync] Triggering initial index refresh...");
                    await RefreshIptvMatchIndexAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BackgroundSync] Index refresh error: {ex.Message}");
                }
            });

            // 2. Continuous Pruning (Independent of sync)
            _ = Task.Run(async () => {
                try
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10));
                        PruneMemoryCache();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BackgroundSync] Pruning error: {ex.Message}");
                }
            });

            // 3. Background Maintenance (Periodic Vacuuming)
            _ = Task.Run(async () => {
                try
                {
                    // Initial delay: 5 minutes after startup to let initialization settle
                    await Task.Delay(TimeSpan.FromMinutes(5));

                    while (true)
                    {
                        var openPlaylistIds = _streamListsCache.Keys
                            .Select(k => k.Split('_')[0])
                            .Distinct()
                            .ToList();

                        if (openPlaylistIds.Count > 0)
                        {
                            AppLogger.Info($"[Maintenance] Starting background vacuum for {openPlaylistIds.Count} playlist(s)...");
                            foreach (var pid in openPlaylistIds)
                            {
                                await VacuumCacheAsync(pid, "vod");
                                await VacuumCacheAsync(pid, "series");
                                await VacuumCacheAsync(pid, "live");
                            }
                            AppLogger.Info("[Maintenance] Background vacuum completed.");
                        }

                        // Periodic cycle every 4 hours
                        await Task.Delay(TimeSpan.FromHours(4));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("[Maintenance] Background vacuum loop error", ex);
                }
            });

            // 4. Start Reactive Sync Loop
            await ScheduleNextSyncAsync();
        }

        private void PruneMemoryCache()
        {
            if (_memoryCache.Count > 50)
            {
                var now = DateTime.UtcNow;
                var keysToRemove = _memoryCache.Where(k => (now - k.Value.LastAccessed).TotalMinutes > 10).Select(k => k.Key).ToList();
                foreach (var key in keysToRemove) _memoryCache.TryRemove(key, out _);
            }
        }

        private async Task ScheduleNextSyncAsync()
        {
            _syncCts?.Cancel();
            _syncCts = new CancellationTokenSource();
            var ct = _syncCts.Token;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!AppSettings.IsAutoCacheEnabled)
                    {
                        // Wait indefinitely until settings change (which will cancel this delay)
                        await Task.Delay(-1, ct);
                        return;
                    }

                    var lastUpdate = AppSettings.LastLiveCacheTime;
                    var interval = TimeSpan.FromMinutes(AppSettings.CacheIntervalMinutes);
                    var nextSync = lastUpdate.Add(interval);
                    var delay = nextSync - DateTime.Now;

                    if (delay <= TimeSpan.Zero)
                    {
                        // Interval passed! Trigger sync.
                        System.Diagnostics.Debug.WriteLine($"[Sync] Threshold reached. Triggering CacheExpired event. (Interval: {AppSettings.CacheIntervalMinutes}m)");
                        CacheExpired?.Invoke(this, EventArgs.Empty);
                        
                        // After trigger, wait for the FULL next interval
                        delay = interval;
                    }

                    System.Diagnostics.Debug.WriteLine($"[Sync] Next sync scheduled in {delay.TotalMinutes:F1} minutes.");
                    await Task.Delay(delay, ct);
                }
            }
            catch (TaskCanceledException) { /* Setting changed, rescheduling... */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] Error in sync loop: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(15), ct); // Fallback retry
            }
        }

        public async Task RefreshIptvMatchIndexAsync(string? playlistId = null)
        {
            var login = App.CurrentLogin;
            if (login == null) return;
            string targetId = playlistId ?? login.PlaylistId ?? AppSettings.LastPlaylistId?.ToString() ?? "default";
            string cid = LifecycleLog.NewId("idx");
            using var lifecycle = LifecycleLog.Begin("Index.Refresh.Legacy", cid, new Dictionary<string, object?>
            {
                ["playlistId"] = targetId,
                ["source"] = "RefreshIptvMatchIndexAsync(string?)"
            });

            await _indexRefreshSemaphore.WaitAsync();
            try
            {
                var swTotal = System.Diagnostics.Stopwatch.StartNew();
                System.Diagnostics.Debug.WriteLine($"[ContentCache] RefreshIptvMatchIndexAsync STARTED for {targetId} at {DateTime.Now}");

                await Task.Run(async () => {
                    IsIndexing = true;
                    AppLogger.Info($"[Index] Refreshing Global Index (Project Zero) for Playlist: {targetId}");
                    try
                    {
                    // 1. Get Current Hashes
                    string vodHash = await ReadTextAsync($"cache_{targetId}_vod.json.gz" + HASH_EXT) ?? "";
                    string seriesHash = await ReadTextAsync($"cache_{targetId}_series.json.gz" + HASH_EXT) ?? "";

                    // 2. Load lists (necessary for mapping)
                    var vods = await LoadCacheAsync<VodStream>(targetId, "vod") ?? new List<VodStream>();
                    var series = await LoadCacheAsync<SeriesStream>(targetId, "series") ?? new List<SeriesStream>();
                    lifecycle.Step("data_loaded", new Dictionary<string, object?> { ["vodCount"] = vods.Count, ["seriesCount"] = series.Count });

                    if (vods.Count == 0 && series.Count == 0) return;

                    // 3. Try Binary Load
                    var result = await LoadMatchIndexBinaryAsync(targetId, vodHash, seriesHash, vods, series);
                    if (result.Success)
                    {
                        lifecycle.Step("binary_loaded", new Dictionary<string, object?>
                        {
                            ["imdbKeys"] = result.Index?.ImdbIndex?.Count ?? 0,
                            ["tokenKeys"] = result.Index?.TokenIndex?.Count ?? 0
                        });
                        AppLogger.Info($"[Index] Project Zero Binary Index Loaded!");
                        return;
                    }

                    AppLogger.Warn($"[Index] Building Index from scratch (Project Zero Build)...");
                    
                    // 4. Build Index (thread-safe aggregation + deterministic merge)
                    var persistImdb = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    var persistToken = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    var mergeLock = new object();

                    void ProcessItems(IReadOnlyList<IMediaStream> items, bool isSeriesType)
                    {
                        System.Threading.Tasks.Parallel.ForEach(System.Linq.Enumerable.Range(0, items.Count),
                            () => (
                                Imdb: new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase),
                                Token: new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase)
                            ),
                            (idx, _, local) =>
                        {
                            var item = items[idx];
                            int packedIdx = isSeriesType ? -(idx + 1) : idx;

                            if (!string.IsNullOrEmpty(item.IMDbId))
                            {
                                string norm = item.IMDbId.Contains(":") ? item.IMDbId.Split(':').Last() : item.IMDbId;
                                if (!local.Imdb.TryGetValue(norm, out var list))
                                {
                                    list = new List<int>(1);
                                    local.Imdb[norm] = list;
                                }
                                list.Add(packedIdx);
                            }

                            if (!string.IsNullOrEmpty(item.Title))
                            {
                                var tokens = TitleHelper.GetSignificantTokens(item.Title);
                                foreach (var t in tokens)
                                {
                                    if (!local.Token.TryGetValue(t, out var list))
                                    {
                                        list = new List<int>(1);
                                        local.Token[t] = list;
                                    }
                                    list.Add(packedIdx);
                                }
                            }
                            return local;
                        },
                        local =>
                        {
                            lock (mergeLock)
                            {
                                foreach (var kvp in local.Imdb)
                                {
                                    if (!persistImdb.TryGetValue(kvp.Key, out var list)) persistImdb[kvp.Key] = kvp.Value;
                                    else list.AddRange(kvp.Value);
                                }
                                foreach (var kvp in local.Token)
                                {
                                    if (!persistToken.TryGetValue(kvp.Key, out var list)) persistToken[kvp.Key] = kvp.Value;
                                    else list.AddRange(kvp.Value);
                                }
                            }
                        });
                    }

                    ProcessItems(vods, false);
                    ProcessItems(series, true);

                    // 5. Binary Save (Atomic & Streamed)
                    await SaveMatchIndexBinaryOptimizedAsync(targetId, vodHash, seriesHash, persistImdb, persistToken);

                    // 6. Update Runtime Service (Direct to ID-Only)
                    // PERFORMANCE: Pre-allocate and build directly to avoid doubling RAM with .ToDictionary copies
                    var imdbRun = new Dictionary<string, int[]>(persistImdb.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in persistImdb) imdbRun[kvp.Key] = kvp.Value.ToArray();
                    persistImdb.Clear(); // Critical: release lists ASAP

                    var tokenRun = new Dictionary<string, int[]>(persistToken.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in persistToken) tokenRun[kvp.Key] = kvp.Value.ToArray();
                    persistToken.Clear(); // Critical: release lists ASAP
                    
                    IptvMatchService.Instance.UpdateIndices(vods, series, imdbRun, tokenRun, cid, "legacy_refresh");
                    lifecycle.Step("published", new Dictionary<string, object?> { ["imdbKeys"] = imdbRun.Count, ["tokenKeys"] = tokenRun.Count });

                    // Final cleanup hint
                    GC.Collect(1, GCCollectionMode.Optimized);
                    }
                    catch (Exception ex) { AppLogger.Error($"[Index] Refresh Failed", ex); }
                    finally 
                    { 
                        IsIndexing = false; 
                        lifecycle.Step("finalize", new Dictionary<string, object?> { ["durationMs"] = swTotal.ElapsedMilliseconds });
                        System.Diagnostics.Debug.WriteLine($"[ContentCache] RefreshIptvMatchIndexAsync COMPLETED in {swTotal.ElapsedMilliseconds}ms");
                    }
                });
            }
            finally
            {
                _indexRefreshSemaphore.Release();
            }
        }

        public Task SyncPlaylistAsync(LoginParams login)
        {
            return SyncInternalAsync(login, force: false);
        }

        public Task SyncNowAsync(LoginParams login)
        {
            return SyncInternalAsync(login, force: true);
        }

        private async Task SyncInternalAsync(LoginParams login, bool force)
        {
            if (login == null) { AppLogger.Warn("[ContentCache] Sync: null login"); return; }
            if (string.IsNullOrEmpty(login.Host)) { AppLogger.Warn("[ContentCache] Sync: No host for Xtream login"); return; }

            string playlistId = login.PlaylistId;
            using var lifecycle = LifecycleLog.Begin("Playlist.Sync", tags: new Dictionary<string, object?>
            {
                ["playlistId"] = playlistId,
                ["force"] = force
            });
            var interval = TimeSpan.FromMinutes(AppSettings.CacheIntervalMinutes);
            
            var lastVod = AppSettings.LastVodCacheTime;
            var lastSeries = AppSettings.LastSeriesCacheTime;
            
            // [FIX] Even if the timestamp is recent, if the RAM cache is empty (likely due to legacy discard), force a sync.
            bool hasVod = _streamListsCache.ContainsKey($"{GetSafePlaylistId(playlistId)}_vod");
            bool hasSeries = _streamListsCache.ContainsKey($"{GetSafePlaylistId(playlistId)}_series");

            bool needsVod = force || (DateTime.Now - lastVod) > interval || (!hasVod && lastVod != DateTime.MinValue);
            bool needsSeries = force || (DateTime.Now - lastSeries) > interval || (!hasSeries && lastSeries != DateTime.MinValue);
            lifecycle.Step("sync_check", new Dictionary<string, object?>
            {
                ["needsVod"] = needsVod,
                ["needsSeries"] = needsSeries,
                ["intervalMin"] = interval.TotalMinutes
            });
            
            AppLogger.Info($"[Checkpoint] [ContentCache] Background Sync Cycle STARTED...");
            var startSw = System.Diagnostics.Stopwatch.StartNew();
            AppLogger.Info($"[ContentCache] Sync Check: playlist={playlistId}, force={force}, lastVod={lastVod}, lastSeries={lastSeries}, interval={interval.TotalMinutes}m, needsVod={needsVod}, needsSeries={needsSeries}");

            if (needsVod || needsSeries)
            {
                AppLogger.Info($"[ContentCache] Background Sync: Fetching data for {login.PlaylistName} (VOD: {needsVod}, Series: {needsSeries})");
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tasks = new List<Task>();

                if (needsVod)
                {
                    tasks.Add(Task.Run(async () => {
                        try
                        {
                            string api = $"{login.Host}/player_api.php?username={login.Username}&password={login.Password}&action=get_vod_streams";
                            using var stream = await HttpHelper.Client.GetStreamAsync(api);
                            
                            var vods = await JsonSerializer.DeserializeAsync<List<VodStream>>(stream, options) ?? new();
                            if (vods.Count > 0)
                            {
                                await SaveCacheAsync(playlistId, "vod", vods);
                                AppSettings.LastVodCacheTime = DateTime.Now;
                            }
                        }
                        catch (Exception ex) { AppLogger.Error("[ContentCache] BG VOD Sync Failed", ex); }
                    }));
                }

                if (needsSeries)
                {
                    tasks.Add(Task.Run(async () => {
                        try
                        {
                            string api = $"{login.Host}/player_api.php?username={login.Username}&password={login.Password}&action=get_series";
                            using var stream = await HttpHelper.Client.GetStreamAsync(api);
                            
                            var series = await JsonSerializer.DeserializeAsync<List<SeriesStream>>(stream, options) ?? new();
                            AppLogger.Info($"[ContentCache] Series Deserialized: {series.Count} items.");
                            
                            if (series.Count > 0)
                            {
                                await SaveCacheAsync(playlistId, "series", series);
                                AppSettings.LastSeriesCacheTime = DateTime.Now;
                            }
                        }
                        catch (Exception ex) { AppLogger.Error("[ContentCache] BG Series Sync Failed", ex); }
                    }));
                }

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                }
            }

            // [PROJECT ZERO] Always ensure index is built/refreshed after sync check
            // This ensures search is active even if we didn't need to fetch new data.
            AppLogger.Info($"[Checkpoint] [ContentCache] Phase 3: Activating Search Index...");
            await RefreshIptvMatchIndexAsync(playlistId, login);
            lifecycle.Step("index_activated");
            
            AppLogger.Info($"[Checkpoint] [ContentCache] Background Sync Cycle FINISHED in {startSw.ElapsedMilliseconds}ms.");

            // [PHASE 2.3] Critical Post-Sync Memory Purge
            // This releases the massive Byte[] buffers back to the OS after the large IO operations.
            _ = Task.Run(async () =>
            {
               await Task.Delay(2000); // Wait for background saves to settle
               AppLogger.Info("[ContentCache] Triggering Post-Sync Memory Purge...");
               
               // Clear Pool and Force GC
               System.Buffers.ArrayPool<byte>.Shared.Return(new byte[0]); // Hint
               GC.Collect(2, GCCollectionMode.Forced, true, true);
               GC.WaitForPendingFinalizers();
               GC.Collect(2, GCCollectionMode.Forced, true, true); // Double pass for finalizers
               
               AppLogger.Info($"[ContentCache] Memory Purge DONE. Managed Heap: {GC.GetTotalMemory(true) / 1024 / 1024}MB");
            });
        }

        public event EventHandler CacheExpired;
        public event EventHandler<string> UpdateAvailable; // Args: Message

        // GENERIC SAVE/LOAD
        
        public async Task<IReadOnlyList<T>> LoadCacheAsync<T>(string playlistId, string category) where T : class
        {
            try
            {
                string safeId = GetSafePlaylistId(playlistId);
                string cacheKey = $"{safeId}_{category}";

                // 1. RAM CACHE CHECK
                if (_streamListsCache.TryGetValue(cacheKey, out var ram)) return ram as IReadOnlyList<T>;

                // 2. PROJECT ZERO: Binary Redirects
                if (category.EndsWith("_categories"))
                {
                    return await LoadCategoriesBinaryAsync(playlistId, category) as IReadOnlyList<T>;
                }
                if (category == "live_streams")
                {
                    return await LoadLiveStreamsBinaryAsync(playlistId) as IReadOnlyList<T>;
                }
                if (category == "vod")
                {
                    return await LoadVodStreamsBinaryAsync(playlistId) as IReadOnlyList<T>;
                }
                if (category == "series")
                {
                    return await LoadSeriesStreamsBinaryAsync(playlistId) as IReadOnlyList<T>;
                }

                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[ContentCache] Load failed for {category}", ex);
                return null;
            }
        }

        // ==========================================
        // PROJECT ZERO - BINARY BUNDLE
        // ==========================================

        public async Task SaveMatchIndexBinaryOptimizedAsync(string playlistId, string vodHash, string seriesHash, Dictionary<string, List<int>> imdb, Dictionary<string, List<int>> token)
        {
            string fileName = $"cache_{playlistId}_match_index.bin.gz";
            string tempName = fileName + ".tmp";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                using (var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting))
                using (var gzip = new GZipStream(fileStream, CompressionLevel.Fastest))
                using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
                {
                    // 1. Header
                    writer.Write("IPTVB"); // Magic
                    writer.Write(2); // Version 2 (Packed Indexes)
                    writer.Write(vodHash ?? "");
                    writer.Write(seriesHash ?? "");

                    // 3. Indices
                    WriteBinaryIndexOptimized(writer, imdb);
                    WriteBinaryIndexOptimized(writer, token);
                    
                    writer.Flush();
                }

                // Atomic Swap
                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
                AppLogger.Info($"[BinarySave] Optimized Index saved to {fileName}.");
            }
            catch (Exception ex) { AppLogger.Error("[BinarySave] Index FAILED", ex); }
            finally { _diskSemaphore.Release(); }
        }

        private void WriteBinaryIndexOptimized(BinaryWriter writer, Dictionary<string, List<int>> index)
        {
            writer.Write(index.Count);
            foreach (var kvp in index)
            {
                writer.Write(kvp.Key);
                writer.Write(kvp.Value.Count);
                foreach (var packedIdx in kvp.Value)
                {
                    writer.Write(packedIdx);
                }
            }
        }

        public async Task<(bool Success, PersistentIptvIndex Index)> LoadMatchIndexBinaryAsync(string playlistId, string vodHash, string seriesHash, IReadOnlyList<VodStream> vods, IReadOnlyList<SeriesStream> series)
        {
            string fileName = $"cache_{playlistId}_match_index.bin.gz";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return (false, null);

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzip, Encoding.UTF8);

                if (reader.ReadString() != "IPTVB") return (false, null);
                int version = reader.ReadInt32();
                string vHash = reader.ReadString();
                string sHash = reader.ReadString();

                if (vHash != vodHash || sHash != seriesHash) return (false, null);

                // 3. Indices (Fast Load)
                Dictionary<string, int[]> imdbRun, tokenRun;
                if (version >= 2)
                {
                    imdbRun = ReadBinaryIndexOptimized(reader);
                    tokenRun = ReadBinaryIndexOptimized(reader);
                }
                else
                {
                    // Legacy conversion (slower but safe for first run after update)
                    imdbRun = ReadBinaryIndexLegacy(reader, vods, series);
                    tokenRun = ReadBinaryIndexLegacy(reader, vods, series);
                }

                IptvMatchService.Instance.UpdateIndices(vods, series, imdbRun, tokenRun);
                return (true, null);
            }
            catch (Exception ex) 
            { 
                AppLogger.Error("[BinaryLoad] FAILED", ex); 
                return (false, null);
            }
            finally { _diskSemaphore.Release(); }
        }

        private Dictionary<string, int[]> ReadBinaryIndexOptimized(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var dict = new Dictionary<string, int[]>(count, StringComparer.OrdinalIgnoreCase);
            
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                int idCount = reader.ReadInt32();
                var ids = new int[idCount];
                for (int j = 0; j < idCount; j++)
                {
                    ids[j] = reader.ReadInt32(); // Already packed list index
                }
                if (ids.Length > 0) dict[key] = ids;
            }
            return dict;
        }

        private Dictionary<string, int[]> ReadBinaryIndexLegacy(BinaryReader reader, IReadOnlyList<VodStream> vods, IReadOnlyList<SeriesStream> series)
        {
            int count = reader.ReadInt32();
            var dict = new Dictionary<string, int[]>(count, StringComparer.OrdinalIgnoreCase);
            
            // For legacy load, we have IDs and need to find the indexes.
            // This is slow, but only happens once when the user updates the app.
            var vodIdMap = new Dictionary<int, int>();
            for(int i=0; i<vods.Count; i++) vodIdMap[vods[i].StreamId] = i;

            var seriesIdMap = new Dictionary<int, int>();
            for(int i=0; i<series.Count; i++) seriesIdMap[series[i].SeriesId] = i;

            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                int idCount = reader.ReadInt32();
                var ids = new List<int>(idCount);
                for (int j = 0; j < idCount; j++)
                {
                    int id = reader.ReadInt32();
                    bool isSeries = reader.ReadBoolean();
                    if (isSeries) {
                        if (seriesIdMap.TryGetValue(id, out int sIdx)) ids.Add(-(sIdx + 1));
                    } else {
                        if (vodIdMap.TryGetValue(id, out int vIdx)) ids.Add(vIdx);
                    }
                }
                if (ids.Count > 0) dict[key] = ids.ToArray();
            }
            return dict;
        }

        // ==========================================
        // PROJECT ZERO - BINARY STREAM BUNDLE (LIVE)
        // ==========================================

        public async Task SaveLiveStreamsBinaryAsync(string playlistId, IReadOnlyList<LiveStream> streams)
        {
            await _diskSemaphore.WaitAsync();
            try { await SaveLiveStreamsBinaryInternalAsync(playlistId, streams); }
            finally { _diskSemaphore.Release(); }
        }

        private async Task SaveLiveStreamsBinaryInternalAsync(string playlistId, IReadOnlyList<LiveStream> streams)
        {
            // UNCOMPRESSED BINARY CACHE (Phase 3.3): GZip adds CPU overhead with minimal savings on struct data.
            // SSDs read 5MB uncompressed faster than 2MB compressed + decompress.
            string fileName = $"cache_{playlistId}_live_streams.bin";
            string tempName = fileName + ".tmp";
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                using (var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting))
                using (var buffered = new BufferedStream(fileStream, 1 * 1024 * 1024)) // 1MB buffer
                using (var writer = new BinaryWriter(buffered, Encoding.UTF8))
                {
                    // 1. Header (32 bytes)
                    writer.Write(0x4256494C); // Magic: "LIVB"
                    writer.Write(2); // Version 2
                    writer.Write(streams.Count);
                    
                    // 2. Metadata Buffer (Strings)
                    byte[] rawBuffer = MetadataBuffer.GetRawBuffer();
                    int bufferPos = MetadataBuffer.GetPosition();
                    writer.Write(bufferPos);

                    // DirtyBit + Padding
                    writer.Write((byte)0); 
                    writer.Write(new byte[15]);

                    writer.Write(rawBuffer, 0, bufferPos);

                    // 3. Streams (PROJECT ZERO: Bulk Write)
                    writer.Write(streams.Count);

                    // Extract structs into a contiguous array for bulk writing
                    var dataArray = new LiveStreamData[streams.Count];
                    for (int i = 0; i < streams.Count; i++) dataArray[i] = streams[i].ToData();

                    // Write the entire struct array as one raw byte blob
                    byte[] structBytes = CastLiveRecordsToBytes(dataArray);
                    writer.Write(structBytes.Length);
                    writer.Write(structBytes);

                    writer.BaseStream.SetLength(writer.BaseStream.Position + 5*1024*1024); writer.Flush();
                    AppLogger.Info($"[BinarySave] Live Streams saved (Atomic). Items: {streams.Count}, Buffer: {bufferPos} bytes.");
                }

                // Atomic Swap
                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
            }
            catch (Exception ex) { AppLogger.Error("[BinarySave] LIVE FAILED", ex); }

            // POPULATE RAM CACHE (Phase 3.1): Instant re-navigation
            _liveRamCache[playlistId] = (streams, null, DateTime.UtcNow);
            string safeId = GetSafePlaylistId(playlistId);
            _streamListsCache[$"{safeId}_live"] = streams;
        }

        public async Task<IReadOnlyList<LiveStream>> LoadLiveStreamsBinaryAsync(string playlistId)
        {
            // RAM CACHE CHECK (Phase 3.1): Instant return if cached
            if (_liveRamCache.TryGetValue(playlistId, out var cached))
            {
                if ((DateTime.UtcNow - cached.Timestamp).TotalMinutes < RAM_CACHE_TTL_MINUTES)
                {
                    AppLogger.Info($"[BinaryLoad] RAM CACHE HIT for {playlistId}. Items: {cached.Streams.Count}");
                    return cached.Streams;
                }
                else
                {
                    _liveRamCache.TryRemove(playlistId, out _);
                }
            }

            // Also check _streamListsCache
            string safeId = GetSafePlaylistId(playlistId);
            if (_streamListsCache.TryGetValue($"{safeId}_live", out var ramCached))
            {
                AppLogger.Info($"[BinaryLoad] STREAM_LISTS CACHE HIT for {playlistId}");
                return ramCached as List<LiveStream>;
            }

            // UNCOMPRESSED BINARY LOAD (Phase 3.3): No GZip decompression
            string fileName = $"cache_{playlistId}_live_streams.bin";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var buffered = new BufferedStream(stream, 1 * 1024 * 1024); // 1MB buffer

                var reader = new BinaryReader(buffered, Encoding.UTF8);

                using (reader)
                {
                    // Magic Check (int is faster than string)
                    int magic = reader.ReadInt32();
                    if (magic != 0x4256494C) return null;
                    int version = reader.ReadInt32();
                    if (version < 2) return null;

                    // 1. Metadata Buffer
                    int count_header = reader.ReadInt32(); // count in header
                    int bufferPos = reader.ReadInt32();
                    
                    // Skip to offset 32 (end of header)
                    reader.BaseStream.Seek(32, SeekOrigin.Begin);
                    
                    byte[] buffer = reader.ReadBytes(bufferPos); 
                    int baseOffset = MetadataBuffer.AppendRawBuffer(buffer, bufferPos);


                    // 2. Streams (PROJECT ZERO: Sync Parsing to allow Span/Casting)
                    int count = reader.ReadInt32();
                    byte[]? structBytes = null;
                    if (version >= 2)
                    {
                        int structBytesLen = reader.ReadInt32();
                        structBytes = reader.ReadBytes(structBytesLen);
                    }

                    var results = ParseLiveStreamDataBulk(structBytes, count, version, reader, baseOffset);

                    // POPULATE RAM CACHE
                    if (results != null)
                    {
                        _liveRamCache[playlistId] = (results, null, DateTime.UtcNow);
                        _streamListsCache[$"{safeId}_live"] = results;
                    }

                    AppLogger.Info($"[BinaryLoad] Live Streams loaded. Items: {results?.Count ?? 0}");
                    return results;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("[BinaryLoad] LIVE FAILED", ex);
                return null;
            }
            finally { _diskSemaphore.Release(); }
        }

        private static List<LiveStream> ParseLiveStreamDataBulk(byte[]? structBytes, int count, int version, BinaryReader reader, int baseOffset)
        {
            var results = new List<LiveStream>(count);
            if (version >= 2 && structBytes != null)
            {
                // Instant conversion from bytes to typed structs
                ReadOnlySpan<LiveStreamData> dataSpan = MemoryMarshal.Cast<byte, LiveStreamData>(structBytes);
                for (int i = 0; i < count; i++)
                {
                    var s = new LiveStream();
                    s.LoadFromData(dataSpan[i], baseOffset);
                    results.Add(s);
                }
            }
            else
            {
                // Fallback for V1 (Legacy)
                for (int i = 0; i < count; i++)
                {
                    var s = new LiveStream();
                    var data = new LiveStreamData();
                    data.StreamId = reader.ReadInt32();
                    data.NameOff = reader.ReadInt32(); data.NameLen = reader.ReadInt32();
                    data.IconOff = reader.ReadInt32(); data.IconLen = reader.ReadInt32();
                    data.ImdbOff = reader.ReadInt32(); data.ImdbLen = reader.ReadInt32();
                    data.DescOff = reader.ReadInt32(); data.DescLen = reader.ReadInt32();
                    data.BgOff = reader.ReadInt32(); data.BgLen = reader.ReadInt32();
                    data.GenreOff = reader.ReadInt32(); data.GenreLen = reader.ReadInt32();
                    data.CastOff = reader.ReadInt32(); data.CastLen = reader.ReadInt32();
                    data.DirOff = reader.ReadInt32(); data.DirLen = reader.ReadInt32();
                    data.TrailOff = reader.ReadInt32(); data.TrailLen = reader.ReadInt32();
                    data.YearOff = reader.ReadInt32(); data.YearLen = reader.ReadInt32();
                    data.ExtOff = reader.ReadInt32(); data.ExtLen = reader.ReadInt32();
                    data.CatOff = reader.ReadInt32(); data.CatLen = reader.ReadInt32();
                    data.RatOff = reader.ReadInt32(); data.RatLen = reader.ReadInt32();
                    s.LoadFromData(data);
                    results.Add(s);
                }
            }
            return results;
        }

        #region VOD & SERIES BINARY (PROJECT ZERO PHASE 2)

        /// <summary>Builds a contiguous UTF-8 heap for binary cache saves so records and string data stay consistent (virtual MMF rows + RO overlays).</summary>
        private sealed class Utf8StringWriter
        {
            private readonly Stream _stream;
            private int _currentOffset = 0;

            public Utf8StringWriter(Stream stream, int startOffset)
            {
                _stream = stream;
                _currentOffset = startOffset;
            }

            public (int Off, int Len) Add(string? s)
            {
                if (string.IsNullOrEmpty(s)) return (-1, 0);
                byte[] utf8 = Encoding.UTF8.GetBytes(s);
                int off = (int)_stream.Position - _currentOffset;
                _stream.Write(utf8, 0, utf8.Length);
                return (off, utf8.Length);
            }

            public int TotalBytesWritten => (int)_stream.Position - _currentOffset;
        }

        private static Models.Metadata.VodRecord BuildVodRecordForBinarySave(VodStream s, Utf8StringWriter heap)
        {
            float ratingValue = 0;
            if (double.TryParse(s.RatingRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                ratingValue = (float)r;

            var name = heap.Add(s.Name);
            var icon = heap.Add(s.IconUrl);
            var imdb = heap.Add(s.ImdbId);
            var plot = heap.Add(s.Description);
            var year = heap.Add(s.Year);
            var genres = heap.Add(s.Genres);
            var cast = heap.Add(s.Cast);
            var dir = heap.Add(s.Director);
            var trail = heap.Add(s.TrailerUrl);
            var bg = heap.Add(s.BackdropUrl);
            var srcTitle = heap.Add(s.SourceTitle);
            var rat = heap.Add(s.RatingRaw);
            var ext = heap.Add(s.ContainerExtension);

            return new Models.Metadata.VodRecord
            {
                StreamId = s.StreamId,
                CategoryId = int.TryParse(s.CategoryId, out int catId) ? catId : 0,
                PriorityScore = s.MetadataPriority,
                Fingerprint = s.CalculateFingerprint(),
                LastModified = DateTime.UtcNow.Ticks,
                NameOff = name.Off, NameLen = name.Len,
                IconOff = icon.Off, IconLen = icon.Len,
                ImdbIdOff = imdb.Off, ImdbIdLen = imdb.Len,
                PlotOff = plot.Off, PlotLen = plot.Len,
                YearOff = year.Off, YearLen = year.Len,
                GenresOff = genres.Off, GenresLen = genres.Len,
                CastOff = cast.Off, CastLen = cast.Len,
                DirectorOff = dir.Off, DirectorLen = dir.Len,
                TrailerOff = trail.Off, TrailerLen = trail.Len,
                BackdropOff = bg.Off, BackdropLen = bg.Len,
                SourceTitleOff = srcTitle.Off, SourceTitleLen = srcTitle.Len,
                RatingOff = rat.Off, RatingLen = rat.Len,
                ExtOff = ext.Off, ExtLen = ext.Len,
                RatingScaled = (short)(ratingValue * 100),
                Flags = s.GetBitFlagsForBinarySave(),
                Duration = 0
            };
        }

        private static Models.Metadata.SeriesRecord BuildSeriesRecordForBinarySave(SeriesStream s, Utf8StringWriter heap)
        {
            float ratingValue = 0;
            if (double.TryParse(s.RatingRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                ratingValue = (float)r;

            var name = heap.Add(s.Name);
            var cover = heap.Add(s.Cover);
            var imdb = heap.Add(s.ImdbId);
            var plot = heap.Add(s.Description);
            var year = heap.Add(s.Year);
            var genres = heap.Add(s.Genres);
            var cast = heap.Add(s.Cast);
            var dir = heap.Add(s.Director);
            var trail = heap.Add(s.TrailerUrl);
            var bg = heap.Add(s.BackdropUrl);
            var srcTitle = heap.Add(s.SourceTitle);
            var rat = heap.Add(s.RatingRaw);
            var ext = heap.Add(s.ContainerExtension);

            return new Models.Metadata.SeriesRecord
            {
                SeriesId = s.SeriesId,
                CategoryId = int.TryParse(s.CategoryId, out int catId) ? catId : 0,
                PriorityScore = s.MetadataPriority,
                Fingerprint = s.CalculateFingerprint(),
                LastModified = DateTime.UtcNow.Ticks,
                NameOff = name.Off, NameLen = name.Len,
                IconOff = cover.Off, IconLen = cover.Len,
                ImdbIdOff = imdb.Off, ImdbIdLen = imdb.Len,
                PlotOff = plot.Off, PlotLen = plot.Len,
                YearOff = year.Off, YearLen = year.Len,
                GenresOff = genres.Off, GenresLen = genres.Len,
                CastOff = cast.Off, CastLen = cast.Len,
                DirectorOff = dir.Off, DirectorLen = dir.Len,
                TrailerOff = trail.Off, TrailerLen = trail.Len,
                BackdropOff = bg.Off, BackdropLen = bg.Len,
                SourceTitleOff = srcTitle.Off, SourceTitleLen = srcTitle.Len,
                RatingOff = rat.Off, RatingLen = rat.Len,
                ExtOff = ext.Off, ExtLen = ext.Len,
                RatingScaled = (short)(ratingValue * 100),
                Flags = s.GetBitFlagsForBinarySave(),
                AirTime = 0
            };
        }
        
        private void WriteVodRecordToStream(BinaryWriter writer, Models.Metadata.VodRecord r)
        {
            writer.Write(r.StreamId);
            writer.Write(r.CategoryId);
            writer.Write(r.PriorityScore);
            writer.Write(r.Fingerprint);
            writer.Write(r.LastModified);
            writer.Write(r.NameOff); writer.Write(r.NameLen);
            writer.Write(r.IconOff); writer.Write(r.IconLen);
            writer.Write(r.ImdbIdOff); writer.Write(r.ImdbIdLen);
            writer.Write(r.PlotOff); writer.Write(r.PlotLen);
            writer.Write(r.YearOff); writer.Write(r.YearLen);
            writer.Write(r.GenresOff); writer.Write(r.GenresLen);
            writer.Write(r.CastOff); writer.Write(r.CastLen);
            writer.Write(r.DirectorOff); writer.Write(r.DirectorLen);
            writer.Write(r.TrailerOff); writer.Write(r.TrailerLen);
            writer.Write(r.BackdropOff); writer.Write(r.BackdropLen);
            writer.Write(r.SourceTitleOff); writer.Write(r.SourceTitleLen);
            writer.Write(r.RatingOff); writer.Write(r.RatingLen);
            writer.Write(r.ExtOff); writer.Write(r.ExtLen);
            writer.Write(r.RatingScaled);
            writer.Write(r.Flags);
            writer.Write(r.Reserved1);
            writer.Write(r.Duration);
        }

        private void WriteSeriesRecordToStream(BinaryWriter writer, Models.Metadata.SeriesRecord r)
        {
            writer.Write(r.SeriesId);
            writer.Write(r.CategoryId);
            writer.Write(r.PriorityScore);
            writer.Write(r.Fingerprint);
            writer.Write(r.LastModified);
            writer.Write(r.NameOff); writer.Write(r.NameLen);
            writer.Write(r.IconOff); writer.Write(r.IconLen);
            writer.Write(r.ImdbIdOff); writer.Write(r.ImdbIdLen);
            writer.Write(r.PlotOff); writer.Write(r.PlotLen);
            writer.Write(r.YearOff); writer.Write(r.YearLen);
            writer.Write(r.GenresOff); writer.Write(r.GenresLen);
            writer.Write(r.CastOff); writer.Write(r.CastLen);
            writer.Write(r.DirectorOff); writer.Write(r.DirectorLen);
            writer.Write(r.TrailerOff); writer.Write(r.TrailerLen);
            writer.Write(r.BackdropOff); writer.Write(r.BackdropLen);
            writer.Write(r.SourceTitleOff); writer.Write(r.SourceTitleLen);
            writer.Write(r.RatingOff); writer.Write(r.RatingLen);
            writer.Write(r.ExtOff); writer.Write(r.ExtLen);
            writer.Write(r.RatingScaled);
            writer.Write(r.Flags);
            writer.Write(r.Reserved1);
            writer.Write(r.AirTime);
        }

        public async Task SaveVodStreamsBinaryAsync(string playlistId, List<VodStream> streams)
        {
            await _diskSemaphore.WaitAsync();
            try { await SaveVodStreamsBinaryInternalAsync(playlistId, streams); }
            finally { _diskSemaphore.Release(); }
        }

        private async Task SaveVodStreamsBinaryInternalAsync(string playlistId, List<VodStream> streams)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_vod.bin";
            string tempName = fileName + ".tmp";
            try
            {
                var folder = ApplicationData.Current.LocalFolder;

                // [LATE DISPOSAL ROOT FIX] Moving TryRemove/Dispose to AFTER the save loop
                // string cacheKey = $"{safeId}_vod";
                // if (_streamListsCache.TryRemove(cacheKey, out var oldSession)) { (oldSession as IDisposable)?.Dispose(); }

                using var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);

                writer.Write(0x564F4444); // Magic
                writer.Write(4);          // Version (v4: Fixed truncation and missing scan)
                writer.Write(streams.Count);
                
                // Placeholder for StringsLen (will update later)
                long stringsLenPos = writer.BaseStream.Position;
                writer.Write(0); 

                writer.Write((byte)1); // DirtyBit (Starting write)
                writer.Write(new byte[15]); // Reserved

                // 2. Sequential Streaming Write
                // Calculate Fixed Offsets: Header(32) + Records + Index
                int recordSize = Marshal.SizeOf<Models.Metadata.VodRecord>();
                int indexEntrySize = 8; // uint(4) + int(4)
                long recordsOffset = 32;
                long indexOffset = recordsOffset + (streams.Count * (long)recordSize);
                long stringsOffset = indexOffset + (streams.Count * (long)indexEntrySize);

                // Prepare Heap Writer at the end of the file
                writer.BaseStream.Seek(stringsOffset, SeekOrigin.Begin);
                var heap = new Utf8StringWriter(writer.BaseStream, (int)stringsOffset);

                // Pass 1: Build Records & Write Strings
                var records = new Models.Metadata.VodRecord[streams.Count];
                var indexEntries = new (uint Fingerprint, int Index)[streams.Count];
                
                for (int i = 0; i < streams.Count; i++)
                {
                    records[i] = BuildVodRecordForBinarySave(streams[i], heap);
                    indexEntries[i] = (records[i].Fingerprint, i);
                }

                int bufferPos = heap.TotalBytesWritten;

                // Pass 2: Write Records (Seek back)
                writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);
                for (int i = 0; i < streams.Count; i++)
                {
                    // Use a direct write to avoid large byte[] allocation from CastToBytes
                    WriteVodRecordToStream(writer, records[i]);
                }

                // Pass 3: Write Index
                writer.BaseStream.Seek(indexOffset, SeekOrigin.Begin);
                Array.Sort(indexEntries, (a, b) => a.Fingerprint.CompareTo(b.Fingerprint));
                for (int i = 0; i < streams.Count; i++)
                {
                    writer.Write(indexEntries[i].Fingerprint);
                    writer.Write(indexEntries[i].Index);
                }

                // 5. Finalize Header
                long finalSize = writer.BaseStream.Position;
                writer.BaseStream.Seek(stringsLenPos, SeekOrigin.Begin);
                writer.Write(bufferPos); 
                writer.BaseStream.Seek(16, SeekOrigin.Begin);
                writer.Write((byte)0); // Mark Clean
                
                writer.Flush();
                writer.Close();

                // 6. Safe disposal of old session BEFORE atomic swap to release file locks
                string cacheKey = $"{safeId}_vod";
                if (_streamListsCache.TryRemove(cacheKey, out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                // 7. Atomic Swap
                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);

                // [PERFORMANCE FIX] Open a temporary session to update the Header Fingerprint
                // This ensures next startup is 0ms.
                using (var finalSession = new BinaryCacheSession(Path.Combine(folder.Path, fileName), 0, 0, 0, streams.Count, 0, readOnlySession: false))
                {
                    long datasetFingerprint = CalculateDatasetFingerprintParallel(streams);
                    finalSession.UpdateHeaderFingerprint(datasetFingerprint);
                    finalSession.UpdateHeaderStringsLen(bufferPos);
                }

                AppLogger.Info($"[BinarySave] VOD Saved (Atomic). Items: {streams.Count}, Buffer: {bufferPos} bytes.");
            }
            catch (Exception ex) { AppLogger.Error("[BinarySave] VOD FAILED", ex); }
        }


        public async Task<IReadOnlyList<VodStream>> LoadVodStreamsBinaryAsync(string playlistId)
        {
            AppLogger.Info($"[Checkpoint] [ContentCache] LoadVodStreamsBinaryAsync for {playlistId}...");
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_vod.bin";
            
            // Check cache key first
            string cacheKey = $"{safeId}_vod";
            if (_streamListsCache.TryGetValue(cacheKey, out var ram)) return ram as IReadOnlyList<VodStream>;

            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                int count, bufferLen, version;
                using (var mmf = BinaryCacheSession.OpenMemoryMappedFile(Path.Combine(folder.Path, fileName), MemoryMappedFileAccess.Read))
                using (var accessor = mmf.CreateViewAccessor(0, 32, MemoryMappedFileAccess.Read))
                {
                    int magic = accessor.ReadInt32(0);
                    if (magic != 0x564F4444) return null;
                    version = accessor.ReadInt32(4);
                    
                    // Force rebuild if version is legacy
                    if (version < 4) 
                    {
                        AppLogger.Warn($"[BinaryLoad] Discarding legacy Version {version} VOD cache.");
                        return null;
                    }

                    count = accessor.ReadInt32(8);
                    bufferLen = accessor.ReadInt32(12);
                }

                // [PHASE 4] 0ms Virtual Binary Session
                int recordSize = Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = 32;
                long indexOffset = recordsOffset + (count * (long)recordSize);
                // VOD index entry is 8 bytes: uint Fingerprint (4) + int Index (4)
                long stringsOffset = indexOffset + (count * 8L);

                var session = new BinaryCacheSession(Path.Combine(folder.Path, fileName), stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                
                // [PERFORMANCE FIX] Read cached fingerprint from header (0ms)
                long cachedFingerprint = session.GetHeaderFingerprint();
                
                var results = new VirtualVodList(session, cachedFingerprint);



                _streamListsCache[cacheKey] = results;
                
                // [NEW] Proactive Index Load: Ensure the search index is ready
                _ = Task.Run(async () => {
                    string matchIdxPath = Path.Combine(folder.Path, $"cache_{safeId}_vod_match.bin");
                    await _vodMatchIndex.LoadAsync(matchIdxPath);
                });

                AppLogger.Info($"[BinaryLoad] VOD Virtual Session Ready: {count} items.");
                return results;
            }
            catch (Exception ex) { AppLogger.Error("[BinaryLoad] VOD Virtual Session FAILED", ex); return null; }
            finally { _diskSemaphore.Release(); }
        }


        public async Task SaveSeriesStreamsBinaryAsync(string playlistId, List<SeriesStream> streams)
        {
            await _diskSemaphore.WaitAsync();
            try { await SaveSeriesStreamsBinaryInternalAsync(playlistId, streams); }
            finally { _diskSemaphore.Release(); }
        }

        private async Task SaveSeriesStreamsBinaryInternalAsync(string playlistId, List<SeriesStream> streams)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_series.bin";
            string tempName = fileName + ".tmp";
            try
            {
                var folder = ApplicationData.Current.LocalFolder;

                // [FIX] Invalidate existing session to release file locks
                // [LATE DISPOSAL ROOT FIX]
                // string cacheKey = $"{safeId}_series";
                // if (_streamListsCache.TryRemove(cacheKey, out var oldSession)) { (oldSession as IDisposable)?.Dispose(); }

                using (var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting))
                using (var writer = new BinaryWriter(fileStream, Encoding.UTF8))
                {
                    writer.Write(0x53455244); // Magic
                    writer.Write(4);          // Version (v4: Fixed truncation and missing scan)
                    writer.Write(streams.Count);
                    
                    long stringsLenPos = writer.BaseStream.Position;
                    writer.Write(0); // Placeholder

                    writer.Write((byte)1); // DirtyBit
                    writer.Write(new byte[15]); // Reserved

                    // Sequential Streaming Write
                    int recordSize = Marshal.SizeOf<Models.Metadata.SeriesRecord>();
                    long recordsOffset = 32;
                    long stringsOffset = recordsOffset + (streams.Count * (long)recordSize);

                    writer.BaseStream.Seek(stringsOffset, SeekOrigin.Begin);
                    var heap = new Utf8StringWriter(writer.BaseStream, (int)stringsOffset);

                    var records = new Models.Metadata.SeriesRecord[streams.Count];
                    for (int i = 0; i < streams.Count; i++)
                    {
                        records[i] = BuildSeriesRecordForBinarySave(streams[i], heap);
                    }

                    int bufferPos = heap.TotalBytesWritten;

                    writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);
                    for (int i = 0; i < streams.Count; i++)
                    {
                        WriteSeriesRecordToStream(writer, records[i]);
                    }

                    long finalSize = writer.BaseStream.Position;
                    writer.BaseStream.Seek(stringsLenPos, SeekOrigin.Begin);
                    writer.Write(bufferPos); 
                    writer.BaseStream.Seek(16, SeekOrigin.Begin);
                    writer.Write((byte)0); // Mark Clean
                    
                    writer.Flush();
                    writer.Close();

                    // Safe disposal of old session BEFORE atomic swap to release file locks
                    string cacheKey = $"{safeId}_series";
                    if (_streamListsCache.TryRemove(cacheKey, out var oldSession))
                    {
                        (oldSession as IDisposable)?.Dispose();
                    }

                    // Atomic Swap
                    var tempFile = await folder.GetFileAsync(tempName);
                    await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);

                    // [PERFORMANCE FIX] Header persistence for 0ms startup
                    using (var finalSession = new BinaryCacheSession(Path.Combine(folder.Path, fileName), 0, 0, 0, streams.Count, 0, readOnlySession: false))
                    {
                        long datasetFingerprint = CalculateDatasetFingerprintParallel(streams);
                        finalSession.UpdateHeaderFingerprint(datasetFingerprint);
                        finalSession.UpdateHeaderStringsLen(bufferPos);
                    }

                    AppLogger.Info($"[BinarySave] Series Saved (Atomic). Items: {streams.Count}, Buffer: {bufferPos} bytes.");
                }
            }
            catch (Exception ex) { AppLogger.Error("[BinarySave] Series FAILED", ex); }
        }

        public async Task<IReadOnlyList<SeriesStream>> LoadSeriesStreamsBinaryAsync(string playlistId)
        {
            AppLogger.Info($"[Checkpoint] [ContentCache] LoadSeriesStreamsBinaryAsync for {playlistId}...");
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_series.bin";
            string cacheKey = $"{safeId}_series";

            if (_streamListsCache.TryGetValue(cacheKey, out var ram)) return ram as IReadOnlyList<SeriesStream>;

            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                int count, bufferLen, version;
                using (var mmf = BinaryCacheSession.OpenMemoryMappedFile(item.Path, MemoryMappedFileAccess.Read))
                using (var accessor = mmf.CreateViewAccessor(0, 32, MemoryMappedFileAccess.Read))
                {
                    int magic = accessor.ReadInt32(0);
                    if (magic != 0x53455244) return null; 
                    version = accessor.ReadInt32(4);
                    if (version < 4)
                    {
                        AppLogger.Warn($"[BinaryLoad] Discarding legacy Version {version} Series cache.");
                        return null; 
                    }

                    count = accessor.ReadInt32(8);
                    bufferLen = accessor.ReadInt32(12);
                }

                // [PHASE 4] 0ms Virtual Binary Session
                int recordSize = Marshal.SizeOf<Models.Metadata.SeriesRecord>();
                long recordsOffset = 32;
                long stringsOffset = recordsOffset + (count * (long)recordSize);

                var session = new BinaryCacheSession(item.Path, stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                
                // [PERFORMANCE FIX] Read cached fingerprint
                long cachedFingerprint = session.GetHeaderFingerprint();
                
                var results = new VirtualSeriesList(session, cachedFingerprint);


                _streamListsCache[cacheKey] = results;
                
                // [NEW] Load Persistent Match Index
                _ = _seriesMatchIndex.LoadAsync(Path.Combine(folder.Path, $"cache_{safeId}_series_match.bin"));

                AppLogger.Info($"[BinaryLoad] Series Virtual Session Ready: {count} items.");
                return results;
            }
            catch (Exception ex) { AppLogger.Error("[BinaryLoad] Series Virtual Session FAILED", ex); return null; }
            finally { _diskSemaphore.Release(); }
        }

        private static byte[] CastLiveRecordsToBytes(LiveStreamData[] records)
        {
            return MemoryMarshal.AsBytes(records.AsSpan()).ToArray();
        }

        private static byte[] CastVodRecordsToBytes(Models.Metadata.VodRecord[] records)
        {
            return MemoryMarshal.AsBytes(records.AsSpan()).ToArray();
        }

        private static byte[] CastSeriesRecordsToBytes(Models.Metadata.SeriesRecord[] records)
        {
            return MemoryMarshal.AsBytes(records.AsSpan()).ToArray();
        }

        #region CATEGORY BINARY (PROJECT ZERO PHASE 4)
        
        public async Task SaveCategoriesBinaryAsync(string playlistId, string categoryType, IReadOnlyList<LiveCategory> categories)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_{categoryType}.bin";
            string tempName = fileName + ".tmp";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;

                // [FIX] Invalidate existing session to release file locks before writing
                // [LATE DISPOSAL ROOT FIX] Moving Dispose to end
                // if (_streamListsCache.TryRemove(cacheKey, out var oldSession)) { (oldSession as IDisposable)?.Dispose(); }

                using var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);

                // 1. Header (32 bytes)
                writer.Write(0x43415443); // Magic
                writer.Write(2);          // Version
                writer.Write(categories.Count);
                
                long stringsLenPos = writer.BaseStream.Position;
                writer.Write(0); // StringsLen Placeholder

                writer.Write((byte)1); // DirtyBit
                writer.Write(new byte[15]); // Reserved
                
                // 2. Construct records and strings in memory first for Categories
                using var stringsMs = new MemoryStream();
                var records = new Models.Metadata.CategoryRecord[categories.Count];
                for (int i = 0; i < categories.Count; i++)
                {
                    var cat = categories[i];
                    byte[] idBytes = Encoding.UTF8.GetBytes(cat.CategoryId ?? "");
                    byte[] nameBytes = Encoding.UTF8.GetBytes(cat.CategoryName ?? "");
                    
                    int idOff = (int)stringsMs.Position;
                    stringsMs.Write(idBytes, 0, idBytes.Length);
                    int idLen = idBytes.Length;
                    
                    int nameOff = (int)stringsMs.Position;
                    stringsMs.Write(nameBytes, 0, nameBytes.Length);
                    int nameLen = nameBytes.Length;
                    
                    records[i] = new Models.Metadata.CategoryRecord { IdOff = idOff, IdLen = idLen, NameOff = nameOff, NameLen = nameLen };
                }

                // 3. Write Records
                long recordsStart = writer.BaseStream.Position;
                byte[] recordBytes = CastCategoryRecordsToBytes(records);
                writer.Write(recordBytes);

                int bufferPos = (int)stringsMs.Length;
                writer.Write(stringsMs.ToArray());

                // Finalize
                long finalSize = writer.BaseStream.Position;
                writer.BaseStream.Position = stringsLenPos;
                writer.Write(bufferPos);
                writer.BaseStream.Position = 16;
                writer.Write((byte)0);

                writer.BaseStream.SetLength(finalSize + 5*1024*1024); writer.Flush();
                writer.Close();

                // 3. Atomic Swap
                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);

                // [LATE DISPOSAL ROOT FIX] Safe disposal
                string cacheKey = $"{safeId}_{categoryType}";
                if (_streamListsCache.TryRemove(cacheKey, out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                AppLogger.Info($"[BinarySave] Categories Saved (Atomic). Items: {categories.Count}, Buffer: {bufferPos} bytes.");
            }
            catch (Exception ex) { AppLogger.Error("[BinarySave] Categories FAILED", ex); }
            finally { _diskSemaphore.Release(); }
        }

        public async Task<IReadOnlyList<LiveCategory>> LoadCategoriesBinaryAsync(string playlistId, string categoryType)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_{categoryType}.bin";
            string cacheKey = $"{safeId}_{categoryType}";

            if (_streamListsCache.TryGetValue(cacheKey, out var ram)) return ram as IReadOnlyList<LiveCategory>;

            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                int count, bufferLen, version;
                using (var mmf = BinaryCacheSession.OpenMemoryMappedFile(item.Path, MemoryMappedFileAccess.Read))
                using (var accessor = mmf.CreateViewAccessor(0, 32, MemoryMappedFileAccess.Read))
                {
                    int magic = accessor.ReadInt32(0);
                    if (magic != 0x43415443) return null;
                    version = accessor.ReadInt32(4);
                    if (version < 2) return null;

                    count = accessor.ReadInt32(8);
                    bufferLen = accessor.ReadInt32(12);
                }

                int recordSize = Marshal.SizeOf<Models.Metadata.CategoryRecord>();
                long recordsOffset = 32;
                long stringsOffset = recordsOffset + (count * (long)recordSize);

                var session = new Helpers.BinaryCacheSession(item.Path, stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                var results = new Helpers.VirtualCategoryList(session);


                _streamListsCache[cacheKey] = results;
                AppLogger.Info($"[BinaryLoad] Categories Virtual Session Ready ({categoryType}): {count} items.");
                return results;
            }
            catch (Exception ex) { AppLogger.Error($"[BinaryLoad] Categories Virtual Session FAILED ({categoryType})", ex); return null; }
            finally { _diskSemaphore.Release(); }
        }

        private static byte[] CastCategoryRecordsToBytes(Models.Metadata.CategoryRecord[] records)
        {
            return MemoryMarshal.AsBytes(records.AsSpan()).ToArray();
        }
        #endregion
        #endregion


        public async Task SaveCacheAsync<T>(string playlistId, string category, IReadOnlyList<T> data) where T : class
        {
            // PROJECT ZERO: Exclusive Binary Persistence
            if (category.EndsWith("_categories") && data is IReadOnlyList<LiveCategory> cats)
            {
                await SaveCategoriesBinaryAsync(playlistId, category, cats);
                return;
            }
            if (category == "live_streams" && data is List<LiveStream> liveStreams)
            {
                await SaveLiveStreamsBinaryAsync(playlistId, liveStreams);
                return;
            }
            if (category == "vod" && data is IReadOnlyList<VodStream> vods)
            {
                var list = vods as List<VodStream> ?? vods.ToList();
                await SaveVodStreamsBinaryAsync(playlistId, list);
                return;
            }
            if (category == "series" && data is IReadOnlyList<SeriesStream> seriesList)
            {
                var list = seriesList as List<SeriesStream> ?? seriesList.ToList();
                await SaveSeriesStreamsBinaryAsync(playlistId, list);
                return;
            }
        }

        private string ComputeHash(Stream stream)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(stream);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private string GetSafePlaylistId(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId)) return "default";
            
            // Check for invalid path characters or URLs
            if (playlistId.Contains("://") || playlistId.Length > 40 || playlistId.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                using (var md5 = MD5.Create())
                {
                    byte[] inputBytes = Encoding.UTF8.GetBytes(playlistId);
                    byte[] hashBytes = md5.ComputeHash(inputBytes);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant().Substring(0, 16);
                }
            }
            return playlistId;
        }

        private async Task<string> ReadTextAsync(string filename)
        {
            try 
            {
                var folder = ApplicationData.Current.LocalFolder;
                var files = await folder.GetFilesAsync();
                if (files.Any(f => f.Name == filename))
                {
                    var file = await folder.GetFileAsync(filename);
                    return await FileIO.ReadTextAsync(file);
                }
                return null;
            }
            catch { return null; }
        }

        private async Task WriteTextAsync(string filename, string content)
        {
            try 
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(file, content);
            }
            catch { }
        }
        
        
        /// <summary>
        /// Convenience overload for background refresh using current login.
        /// </summary>
        public async Task RefreshIptvMatchIndexAsync()
        {
            var login = App.CurrentLogin;
            if (login == null) return;
            await RefreshIptvMatchIndexAsync(login.PlaylistId, login);
        }

        public async Task RefreshIptvMatchIndexAsync(string playlistId, LoginParams login)
        {
            await _indexRefreshSemaphore.WaitAsync();
            try
            {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string safeId = GetSafePlaylistId(playlistId);
            var folder = ApplicationData.Current.LocalFolder;
            string cid = LifecycleLog.NewId("idx");
            using var lifecycle = LifecycleLog.Begin("Index.Refresh.Virtual", cid, new Dictionary<string, object?>
            {
                ["playlistId"] = playlistId,
                ["safeId"] = safeId,
                ["source"] = "RefreshIptvMatchIndexAsync(string,LoginParams)"
            });
            
            // 1. Get Virtual Lists (Try RAM cache first, else load from disk)
            Helpers.VirtualVodList? vvl = null;
            if (_streamListsCache.TryGetValue($"{safeId}_vod", out var vodList) && vodList is Helpers.VirtualVodList vvlCached)
            {
                vvl = vvlCached;
            }
            else
            {
                var loaded = await LoadVodStreamsBinaryAsync(playlistId);
                vvl = loaded as Helpers.VirtualVodList;
                if (vvl == null) AppLogger.Warn($"[MatchIndex] VOD data missing or stale on disk for {playlistId}");
            }

            Helpers.VirtualSeriesList? vsl = null;
            if (_streamListsCache.TryGetValue($"{safeId}_series", out var serList) && serList is Helpers.VirtualSeriesList vslCached)
            {
                vsl = vslCached;
            }
            else
            {
                var loaded = await LoadSeriesStreamsBinaryAsync(playlistId);
                vsl = loaded as Helpers.VirtualSeriesList;
                if (vsl == null) AppLogger.Warn($"[MatchIndex] Series data missing or stale on disk for {playlistId}");
            }

            if (vvl == null && vsl == null)
            {
                AppLogger.Warn($"[MatchIndex] ABORT: Both VOD and Series data missing on disk for {playlistId}");
                return;
            }

            lifecycle.Step("lists_ready", new Dictionary<string, object?> { ["vodCount"] = vvl?.Count ?? 0, ["seriesCount"] = vsl?.Count ?? 0 });

            AppLogger.Info($"[Checkpoint] [MatchIndex] Phase 1: Validating Local Search Indices for {safeId}...");
            
            // 2. SMART LOAD: Check VOD Index
            if (vvl != null)
            {
                string vodIdxPath = Path.Combine(folder.Path, $"cache_{safeId}_vod_match.bin");
                bool vodLoaded = await _vodMatchIndex.LoadAsync(vodIdxPath);
                if (!vodLoaded || _vodMatchIndex.SourceFingerprint != vvl.Fingerprint)
                {
                    AppLogger.Info($"[MatchIndex] VOD Index Stale or Missing (Disk:{_vodMatchIndex.SourceFingerprint} vs RAM:{vvl.Fingerprint}). Rebuilding...");
                    _vodMatchIndex.Build(vvl);
                    await _vodMatchIndex.SaveAsync(vodIdxPath);
                    lifecycle.Step("vod_rebuilt");
                }
                else AppLogger.Info($"[MatchIndex] VOD Index Valid (Fingerprint: {_vodMatchIndex.SourceFingerprint}). Loaded from disk.");
            }

            // 3. SMART LOAD: Check Series Index
            if (vsl != null)
            {
                string serIdxPath = Path.Combine(folder.Path, $"cache_{safeId}_series_match.bin");
                bool serLoaded = await _seriesMatchIndex.LoadAsync(serIdxPath);
                if (!serLoaded || _seriesMatchIndex.SourceFingerprint != vsl.Fingerprint)
                {
                    AppLogger.Info($"[MatchIndex] Series Index Stale or Missing (Disk:{_seriesMatchIndex.SourceFingerprint} vs RAM:{vsl.Fingerprint}). Rebuilding...");
                    _seriesMatchIndex.Build(vsl);
                    await _seriesMatchIndex.SaveAsync(serIdxPath);
                    lifecycle.Step("series_rebuilt");
                }
                else AppLogger.Info($"[MatchIndex] Series Index Valid (Fingerprint: {_seriesMatchIndex.SourceFingerprint}). Loaded from disk.");
            }

            // 4. PUBLISH TO GLOBAL SERVICE (Project Zero Activation)
            AppLogger.Info($"[Checkpoint] [MatchIndex] Phase 2: Synchronizing with Global Search Service...");
            
            var vodTokenMap = _vodMatchIndex.GetTokenMap();
            var serTokenMap = _seriesMatchIndex.GetTokenMap();
            var vodIdMap = _vodMatchIndex.GetIdMap();
            var serIdMap = _seriesMatchIndex.GetIdMap();

            // Merge into unified indices for IptvMatchService
            var imdbIndex = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            var tokenIndex = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

            // Merge IDs (imdbId -> streamIds[])
            foreach (var kvp in vodIdMap) imdbIndex[kvp.Key] = kvp.Value;
            foreach (var kvp in serIdMap) 
            {
                var packedSer = kvp.Value.Select(i => -i).ToArray();
                if (imdbIndex.TryGetValue(kvp.Key, out var existing))
                {
                    var newArr = new int[existing.Length + packedSer.Length];
                    Array.Copy(existing, newArr, existing.Length);
                    Array.Copy(packedSer, 0, newArr, existing.Length, packedSer.Length);
                    imdbIndex[kvp.Key] = newArr;
                }
                else imdbIndex[kvp.Key] = packedSer;
            }

            // Merge Tokens
            foreach (var kvp in vodTokenMap) tokenIndex[kvp.Key] = kvp.Value;
            foreach (var kvp in serTokenMap)
            {
                var packedSer = kvp.Value.Select(i => -i).ToArray();
                if (tokenIndex.TryGetValue(kvp.Key, out var existing))
                {
                    var newArr = new int[existing.Length + packedSer.Length];
                    Array.Copy(existing, newArr, existing.Length);
                    Array.Copy(packedSer, 0, newArr, existing.Length, packedSer.Length);
                    tokenIndex[kvp.Key] = newArr;
                }
                else tokenIndex[kvp.Key] = packedSer;
            }

            IptvMatchService.Instance.UpdateIndices(vvl, vsl, imdbIndex, tokenIndex, cid, "virtual_refresh");
            lifecycle.Step("published", new Dictionary<string, object?> { ["imdbKeys"] = imdbIndex.Count, ["tokenKeys"] = tokenIndex.Count });

            AppLogger.Info($"[Checkpoint] [MatchIndex] ✅ LOCAL SEARCH ACTIVATED in {sw.ElapsedMilliseconds}ms. Tokens: {tokenIndex.Count}, Streams: {(vvl?.Count ?? 0) + (vsl?.Count ?? 0)}");
            }
            finally
            {
                _indexRefreshSemaphore.Release();
            }
        }

        public async Task<List<IMediaStream>> FindLocalMatchesAsync(string title, string? imdbId = null)
        {
            var results = new List<IMediaStream>();

            // --- STAGE 1: ID MATCHING (%100 Accurate) ---
            if (!string.IsNullOrEmpty(imdbId))
            {
                // Check VOD ID Index
                int[] vodIdsFound = _vodMatchIndex.FindById(imdbId);
                if (vodIdsFound.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_vod", out var vodList) && vodList is VirtualVodList vvl)
                {
                    foreach (var vid in vodIdsFound)
                    {
                        var match = vvl.FirstOrDefault(v => v.StreamId == vid);
                        if (match != null) results.Add(match);
                    }
                }

                // Check Series ID Index
                int[] serIdsFound = _seriesMatchIndex.FindById(imdbId);
                if (serIdsFound.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_series", out var serList) && serList is VirtualSeriesList vsl)
                {
                    foreach (var sid in serIdsFound)
                    {
                        var match = vsl.FirstOrDefault(s => s.SeriesId == sid);
                        if (match != null) results.Add(match);
                    }
                }

                // If we found an absolute ID match, we stop here (Stage 2 not needed)
                if (results.Count > 0) return results;
            }

            // --- STAGE 2: TOKEN MATCHING (Alternative Fallback) ---
            var tokens = TitleHelper.GetSignificantTokens(title);
            if (tokens.Count == 0) return results;

            // Check VOD Token Index
            var vodIds = _vodMatchIndex.FindByTokens(tokens);
            if (vodIds.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_vod", out var vodList2) && vodList2 is VirtualVodList vvl2)
            {
                foreach (var id in vodIds)
                {
                    var match = vvl2.FirstOrDefault(v => v.StreamId == id);
                    if (match != null) results.Add(match);
                }
            }

            // Check Series Token Index
            var serIds = _seriesMatchIndex.FindByTokens(tokens);
            if (serIds.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_series", out var serList2) && serList2 is VirtualSeriesList vsl2)
            {
                foreach (var id in serIds)
                {
                    var match = vsl2.FirstOrDefault(s => s.SeriesId == id);
                    if (match != null) results.Add(match);
                }
            }

            return results;
        }
        
        public async Task ClearCacheAsync()
        {
            try
            {
                _memoryCache.Clear(); // Clear RAM
                _streamListsCache.Clear(); // [FIX] Reset RAM streams cache
                MetadataBuffer.Reset(); // [FIX] Clear Project Zero global buffer

                var folder = ApplicationData.Current.LocalFolder;
                var files = await folder.GetFilesAsync();
                foreach (var file in files)
                {
                    // PROJECT ZERO: Clean up both legacy JSON and new Binary formats
                    if (file.Name.StartsWith("cache_") && 
                       (file.Name.EndsWith(".json.gz") || file.Name.EndsWith(".hash") || file.Name.EndsWith(".bin") || file.Name.EndsWith(".bin.gz")))
                    {
                        await file.DeleteAsync();
                    }
                }
                
                // Reset Timestamps
                AppSettings.LastLiveCacheTime = DateTime.MinValue;
                AppSettings.LastVodCacheTime = DateTime.MinValue;
                AppSettings.LastSeriesCacheTime = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[ContentCache] Clear Failed: {ex.Message}");
            }
        }

        // SEARCH INDEXING
        public void BuildSearchIndex(string category, IEnumerable<dynamic> items)
        {
            // Background Task
            Task.Run(() => 
            {
                // Simple implementation: Just store reference or build trie
            });
        }
        // SERIES INFO CACHING
        public async Task<SeriesInfoResult> GetSeriesInfoAsync(int seriesId, LoginParams login)
        {
            SeriesInfoResult binarySeed = null;
            try {
                // [PHASE 4] Binary-First Optimization for Series Metadata
                string pid = login.PlaylistUrl ?? "default";
                string safeId = GetSafePlaylistId(pid);
                if (_streamListsCache.TryGetValue($"{safeId}_series", out var cachedList) && cachedList is Helpers.VirtualSeriesList vsl) {
                    var stream = vsl.FirstOrDefault(s => s.SeriesId == seriesId);
                    if (stream != null && stream.PriorityScore > 4000) {
                        binarySeed = new SeriesInfoResult {
                            Info = new SeriesInfoDetails {
                                Name = stream.Name,
                                Plot = stream.Description,
                                Cast = stream.Cast,
                                Director = stream.Director,
                                Genre = stream.Genre,
                                Rating = stream.Rating,
                                ReleaseDate = stream.Year,
                                Cover = stream.Cover,
                                BackdropPath = stream.BackdropPath?.ToArray() ?? Array.Empty<string>(),
                                YoutubeTrailer = stream.YoutubeTrailer
                            }
                        };
                    }
                }
            } catch { }
            string cacheKey = $"series_info_{seriesId}";
            string playlistId = login.PlaylistUrl ?? "default";

            // 0. SEARCH MEMORY CACHE
            if (_memoryCache.TryGetValue(cacheKey, out var memEntry))
            {
                // Update access time
                _memoryCache[cacheKey] = (memEntry.Data, DateTime.UtcNow);
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "RAM HIT", cacheKey);
                return memEntry.Data as SeriesInfoResult;
            }
            
            // 1. Try Disk Cache
            var cached = await LoadCacheObjectAsync<SeriesInfoResult>(playlistId, cacheKey);

            if (cached != null) 
            {
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "DISK HIT", cacheKey);
                // [LOG FOR USER] Log the cached data as JSON
                try {
                    string cachedJson = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
                    AppLogger.Warn($"[IPTV_CACHE_SERIES] SeriesId: {seriesId} | CACHED JSON: {cachedJson}");
                } catch { }

                // Promote to RAM
                _memoryCache[cacheKey] = (cached, DateTime.UtcNow);
                return cached;
            }
            
            System.Diagnostics.Debug.WriteLine($"[ContentCache] MISS for key: {cacheKey}. Fetching from network...");

            // 2. Fetch Network
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string url = $"{login.Host.TrimEnd('/')}/player_api.php?username={login.Username}&password={login.Password}&action=get_series_info&series_id={seriesId}";
                using var client = new System.Net.Http.HttpClient();
                var json = await client.GetStringAsync(url);
                
                // [NEW] Robust RAW Debug Log
                await WriteDebugJsonAsync($"series_{seriesId}_raw.json", json);
                AppLogger.Warn($"[IPTV_RAW_SERIES] SeriesId: {seriesId} | Raw JSON saved to debug file.");

                sw.Stop();
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "Network Fetch", $"{sw.ElapsedMilliseconds}ms | {json.Length} bytes");
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                var result = JsonSerializer.Deserialize<SeriesInfoResult>(json, options);

                if (result != null)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Content, "Fetch Success", $"Episodes: {result.Episodes?.Count ?? 0}");
                    
                    // Save Disk + RAM
                    _memoryCache[cacheKey] = (result, DateTime.UtcNow); // RAM
                    await SaveSingularCacheAsync(playlistId, cacheKey, result); // Disk

                    return result;
                }
                else
                {
                     Services.CacheLogger.Warning(Services.CacheLogger.Category.Content, "Fetch Result NULL");
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.Content, "SeriesFetch Error", ex.Message);
            }

            return null;
        }


        // VOD INFO CACHING
        public async Task<MovieInfoResult> GetMovieInfoAsync(int streamId, LoginParams login)
        {
            try {
                // [PHASE 4] Binary-First Optimization: If already in our high-speed index, skip JSON load.
                string pid = login.PlaylistUrl ?? "default";
                string safeId = GetSafePlaylistId(pid);
                if (_streamListsCache.TryGetValue($"{safeId}_vod", out var cachedList) && cachedList is Helpers.VirtualVodList vvl) {
                    var stream = vvl.FirstOrDefault(s => s.StreamId == streamId);
                    if (stream != null && stream.PriorityScore > 4000) {
                        return new MovieInfoResult {
                            Info = new MovieInfoDetails {
                                Name = stream.Title,
                                Plot = stream.Description,
                                Cast = stream.Cast,
                                Director = stream.Director,
                                Genre = stream.Genres,
                                RatingRaw = stream.Rating,
                                ReleaseDate = stream.Year,
                                MovieImage = stream.PosterUrl,
                                BackdropPath = stream.BackdropUrl?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                                YoutubeTrailer = stream.TrailerUrl,
                                ContainerExtension = stream.ContainerExtension // CRITICAL for playback
                            }
                        };
                    }
                }
            } catch { }
            string cacheKey = $"vod_info_{streamId}";
            string playlistId = login.PlaylistUrl ?? "default";

            // 0. SEARCH MEMORY CACHE
            if (_memoryCache.TryGetValue(cacheKey, out var memEntry))
            {
                _memoryCache[cacheKey] = (memEntry.Data, DateTime.UtcNow);
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "RAM HIT", cacheKey);
                return memEntry.Data as MovieInfoResult;
            }
            
            // 1. Try Disk Cache
            var cached = await LoadCacheObjectAsync<MovieInfoResult>(playlistId, cacheKey);
            if (cached != null) 
            {
                Services.CacheLogger.Info(Services.CacheLogger.Category.Content, "DISK HIT", cacheKey);
                // [LOG FOR USER] Log the cached data as JSON
                try {
                    string cachedJson = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
                    AppLogger.Warn($"[IPTV_CACHE_VOD] StreamId: {streamId} | CACHED JSON: {cachedJson}");
                } catch { }

                _memoryCache[cacheKey] = (cached, DateTime.UtcNow);
                return cached;
            }
            
            // 2. Fetch Network
            try
            {
                string url = $"{login.Host.TrimEnd('/')}/player_api.php?username={login.Username}&password={login.Password}&action=get_vod_info&vod_id={streamId}";
                using var client = new System.Net.Http.HttpClient();
                var json = await client.GetStringAsync(url);
                
                // [NEW] Robust RAW Debug Log
                await WriteDebugJsonAsync($"vod_{streamId}_raw.json", json);
                AppLogger.Warn($"[IPTV_RAW_VOD] StreamId: {streamId} | Raw JSON saved to debug file.");
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
                var result = JsonSerializer.Deserialize<MovieInfoResult>(json, options);

                if (result != null)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Content, "Fetch Success", $"Movie: {result.Info?.Name ?? "Unknown"}");
                    
                    // Save Disk + RAM
                    _memoryCache[cacheKey] = (result, DateTime.UtcNow);
                    await SaveSingularCacheAsync(playlistId, cacheKey, result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.Content, "VodFetch Error", ex.Message);
            }

            return null;
        }
        // Moved to bottom for organization

        private async Task WriteDebugJsonAsync(string filename, string content)
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var debugFolder = await folder.CreateFolderAsync("DebugLogs", CreationCollisionOption.OpenIfExists);
                var file = await debugFolder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, content);
                
                // Also log first 500 chars to console just in case
                string snippet = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                System.Diagnostics.Debug.WriteLine($"[DEBUG_RAW_JSON] {filename}: {snippet}");
            }
            catch { }
        }

        // ==========================================
        // MATCH PERSISTENCE (THROTTLED)
        // ==========================================

        public void RegisterImdbMapping(int streamId, string imdbId, bool isSeries)
        {
            if (string.IsNullOrEmpty(imdbId)) return;

            string category = isSeries ? "series" : "vod";
            string playlistId = App.CurrentLogin?.PlaylistId ?? AppSettings.LastPlaylistId?.ToString() ?? "default";
            string safeId = GetSafePlaylistId(playlistId);
            string cacheKey = $"{safeId}_{category}";

            if (_streamListsCache.TryGetValue(cacheKey, out var cached) && cached is System.Collections.IEnumerable list)
            {
                foreach (var item in list)
                {
                    if (isSeries && item is SeriesStream series && series.SeriesId == streamId)
                    {
                        if (series.ImdbId == imdbId) return;
                        series.ImdbId = imdbId;
                        AppLogger.Info($"[ContentCache] Mapping LEARNED globally: {category} ID {streamId} -> IMDb {imdbId}. Queueing save...");
                        _pendingSaveCategories.TryAdd(category, 0);
                        _throttledSaveTimer.Change(5000, -1); // Save in 5 seconds to batch
                        return;
                    }
                    else if (!isSeries && item is VodStream vod && vod.StreamId == streamId)
                    {
                        if (vod.ImdbId == imdbId) return;
                        vod.ImdbId = imdbId;
                        AppLogger.Info($"[ContentCache] Mapping LEARNED globally: {category} ID {streamId} -> IMDb {imdbId}. Queueing save...");
                        _pendingSaveCategories.TryAdd(category, 0);
                        _throttledSaveTimer.Change(5000, -1);
                        return;
                    }
                }
            }
        }

        public Task TriggerThrottledSaveAsync(string category)
        {
            _pendingSaveCategories.TryAdd(category, 0);
            _throttledSaveTimer.Change(15000, -1); // Save in 15 seconds to batch multiple matches
            return Task.CompletedTask;
        }

        private async Task ProcessThrottledSavesAsync()
        {
            var categories = _pendingSaveCategories.Keys.ToList();
            foreach (var c in categories)
                _pendingSaveCategories.TryRemove(c, out _);

            var login = App.CurrentLogin;
            if (login == null) return;

            AppLogger.Info($"[ContentCache] Processing throttled saves for categories: {string.Join(", ", categories)}");

            foreach (var cat in categories)
            {
                try
                {
                    Interlocked.Increment(ref ThrottledSaveAttempts);
                    string safeId = GetSafePlaylistId(login.PlaylistId);
                    string cacheKey = $"{safeId}_{cat}";

                    if (_streamListsCache.TryGetValue(cacheKey, out var data))
                    {
                        if (cat == "vod" && data is IReadOnlyList<VodStream> vodRo)
                            await SaveCacheAsync(login.PlaylistId, "vod", vodRo);
                        else if (cat == "series" && data is IReadOnlyList<SeriesStream> seriesRo)
                            await SaveCacheAsync(login.PlaylistId, "series", seriesRo);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref ThrottledSaveFailures);
                    AppLogger.Error($"[ContentCache] Throttled Save Error ({cat}): {ex.Message}");
                }
            }

            if (!_pendingSaveCategories.IsEmpty)
                _throttledSaveTimer.Change(2000, Timeout.Infinite);
        }
        public async Task<T> LoadCacheObjectAsync<T>(string playlistId, string key) where T : class
        {
            // [PROJECT ZERO] Specialized Binary Loaders for Singleton Metadata
            if (typeof(T) == typeof(MovieInfoResult))
            {
                return await LoadMovieInfoBinaryAsync(key) as T;
            }
            if (typeof(T) == typeof(SeriesInfoResult))
            {
                return await LoadSeriesInfoBinaryAsync(key) as T;
            }

            return default;
        }

        private async Task SaveSingularCacheAsync<T>(string playlistId, string key, T data) where T : class
        {
            // [PROJECT ZERO] Specialized Binary Savers
            if (data is MovieInfoResult movieInfo)
            {
                await SaveMovieInfoBinaryAsync(key, movieInfo);
            }
            else if (data is SeriesInfoResult seriesInfo)
            {
                await SaveSeriesInfoBinaryAsync(key, seriesInfo);
            }
        }

        #region MOVIE & SERIES INFO BINARY (PROJECT ZERO PHASE 5)
        
        private async Task SaveMovieInfoBinaryAsync(string key, MovieInfoResult data)
        {
            string fileName = $"cache_{key}.bin";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                using var fileStream = await folder.OpenStreamForWriteAsync(fileName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);

                writer.Write(0x4D4F5649); // Magic: "MOVI"
                writer.Write(1);          // Version

                // Info Section
                writer.Write(data.Info?.Name ?? "");
                writer.Write(data.Info?.MovieImage ?? "");
                writer.Write(data.Info?.CoverBig ?? "");
                writer.Write(data.Info?.Plot ?? "");
                writer.Write(data.Info?.Cast ?? "");
                writer.Write(data.Info?.Genre ?? "");
                writer.Write(data.Info?.Director ?? "");
                writer.Write(data.Info?.Rating ?? "");
                writer.Write(data.Info?.ReleaseDate ?? "");
                writer.Write(data.Info?.YoutubeTrailer ?? "");
                writer.Write(data.Info?.Duration ?? "");

                // MovieData Section
                writer.Write(data.MovieData?.StreamId ?? 0);
                writer.Write(data.MovieData?.ContainerExtension ?? "");

                AppLogger.Info($"[BinarySave] MovieInfo saved: {key}");
            }
            catch (Exception ex) { AppLogger.Error($"[BinarySave] MovieInfo FAILED: {key}", ex); }
            finally { _diskSemaphore.Release(); }
        }

        private async Task<MovieInfoResult> LoadMovieInfoBinaryAsync(string key)
        {
            string fileName = $"cache_{key}.bin";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var reader = new BinaryReader(stream, Encoding.UTF8);

                if (reader.ReadInt32() != 0x4D4F5649) return null;
                int version = reader.ReadInt32();

                var result = new MovieInfoResult();
                result.Info = new MovieInfoDetails
                {
                    Name = reader.ReadString(),
                    MovieImage = reader.ReadString(),
                    CoverBig = reader.ReadString(),
                    Plot = reader.ReadString(),
                    Cast = reader.ReadString(),
                    Genre = reader.ReadString(),
                    Director = reader.ReadString(),
                    RatingRaw = reader.ReadString(),
                    ReleaseDate = reader.ReadString(),
                    YoutubeTrailer = reader.ReadString(),
                    Duration = reader.ReadString()
                };

                result.MovieData = new MovieDataDetails
                {
                    StreamId = reader.ReadInt32(),
                    ContainerExtension = reader.ReadString()
                };

                return result;
            }
            catch { return null; }
            finally { _diskSemaphore.Release(); }
        }

        private async Task SaveSeriesInfoBinaryAsync(string key, SeriesInfoResult data)
        {
            string fileName = $"cache_{key}.bin";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                using var fileStream = await folder.OpenStreamForWriteAsync(fileName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);

                writer.Write(0x53455249); // Magic: "SERI"
                writer.Write(1);          // Version

                // Info Section
                writer.Write(data.Info?.Name ?? "");
                writer.Write(data.Info?.TmdbId?.ToString() ?? "");
                writer.Write(data.Info?.Cover ?? "");
                writer.Write(data.Info?.Plot ?? "");
                writer.Write(data.Info?.Cast ?? "");
                writer.Write(data.Info?.Genre ?? "");
                writer.Write(data.Info?.Director ?? "");
                writer.Write(data.Info?.Rating ?? "");
                writer.Write(data.Info?.ReleaseDate ?? "");

                // Episodes Section (Dictionary Serialization)
                int seasonCount = data.Episodes?.Count ?? 0;
                writer.Write(seasonCount);
                if (data.Episodes != null)
                {
                    foreach (var season in data.Episodes)
                    {
                        writer.Write(season.Key); // Season identifier
                        writer.Write(season.Value.Count);
                        foreach (var ep in season.Value)
                        {
                            writer.Write(ep.Id ?? "");
                            writer.Write(ep.Title ?? "");
                            writer.Write(ep.EpisodeNum?.ToString() ?? "");
                            writer.Write(ep.Season?.ToString() ?? "");
                            writer.Write(ep.ContainerExtension ?? "");
                            writer.Write(ep.Info?.MovieImage ?? "");
                            writer.Write(ep.Info?.Plot ?? "");
                        }
                    }
                }

                AppLogger.Info($"[BinarySave] SeriesInfo saved: {key}");
            }
            catch (Exception ex) { AppLogger.Error($"[BinarySave] SeriesInfo FAILED: {key}", ex); }
            finally { _diskSemaphore.Release(); }
        }

        private async Task<SeriesInfoResult> LoadSeriesInfoBinaryAsync(string key)
        {
            string fileName = $"cache_{key}.bin";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var reader = new BinaryReader(stream, Encoding.UTF8);

                if (reader.ReadInt32() != 0x53455249) return null;
                int version = reader.ReadInt32();

                var result = new SeriesInfoResult();
                result.Info = new SeriesInfoDetails
                {
                    Name = reader.ReadString(),
                    TmdbId = reader.ReadString(),
                    Cover = reader.ReadString(),
                    Plot = reader.ReadString(),
                    Cast = reader.ReadString(),
                    Genre = reader.ReadString(),
                    Director = reader.ReadString(),
                    Rating = reader.ReadString(),
                    ReleaseDate = reader.ReadString()
                };

                int seasonCount = reader.ReadInt32();
                result.Episodes = new Dictionary<string, List<SeriesEpisodeDef>>(seasonCount);
                for (int i = 0; i < seasonCount; i++)
                {
                    string seasonKey = reader.ReadString();
                    int epCount = reader.ReadInt32();
                    var episodes = new List<SeriesEpisodeDef>(epCount);
                    for (int j = 0; j < epCount; j++)
                    {
                        var ep = new SeriesEpisodeDef
                        {
                            Id = reader.ReadString(),
                            Title = reader.ReadString(),
                            EpisodeNum = reader.ReadString(),
                            Season = reader.ReadString(),
                            ContainerExtension = reader.ReadString(),
                            Info = new SeriesEpisodeInfo
                            {
                                MovieImage = reader.ReadString(),
                                Plot = reader.ReadString()
                            }
                        };
                        episodes.Add(ep);
                    }
                    result.Episodes[seasonKey] = episodes;
                }

                return result;
            }
            catch { return null; }
            finally { _diskSemaphore.Release(); }
        }
        #endregion

        #region VACUUM & DEFRAGMENTATION (PROJECT ZERO PHASE 4)
        
        /// <summary>
        /// Global shutdown cleanup for PROJECT ZERO.
        /// </summary>
        public async Task ShutdownAsync()
        {
            AppLogger.Info("[ContentCacheService] System Shutdown initiated.");
            
            var openPlaylistIds = _streamListsCache.Keys
                .Select(k => k.Split('_')[0])
                .Distinct()
                .ToList();

            AppLogger.Info($"[ContentCacheService] Found {openPlaylistIds.Count} open playlists to flush: {string.Join(", ", openPlaylistIds)}");

            // [OPTIMIZATION] Skip blocking Vacuum on shutdown. 
            // In-Place Hydration already handles real-time persistence.
            // Full vacuum is now handled by background maintenance loop.

            foreach (var session in _streamListsCache.Values)
            {
                (session as IDisposable)?.Dispose();
            }
            _streamListsCache.Clear();
            AppLogger.Info("[ContentCacheService] System Shutdown completed.");
        }

        public async Task VacuumCacheAsync(string playlistId, string category)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_{category}.bin";
            string cacheKey = $"{safeId}_{category}";

            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                if (!_streamListsCache.TryGetValue(cacheKey, out var data)) return;

                AppLogger.Info($"[Vacuum] Starting for {category}...");

                // [ROOT FIX] Guard against AccessViolation by Incrementing the RefCount
                // This ensures the MMF stays mapped even if the UI decidies to 'invalidate' it during vacuum.
                bool hasRef = false;
                if (data is Helpers.VirtualVodList vVod) { vVod.AddRef(); hasRef = true; }
                else if (data is Helpers.VirtualSeriesList vSeries) { vSeries.AddRef(); hasRef = true; }
                else if (data is IDisposable disp) { /* Generic fallback if needed */ }

                try 
                {
                    if (category == "vod" && data is IReadOnlyList<VodStream> vods)
                    {
                        var list = vods as List<VodStream> ?? vods.ToList();
                        await SaveVodStreamsBinaryInternalAsync(playlistId, list);
                    }
                    else if (category == "series" && data is IReadOnlyList<SeriesStream> seriesRo)
                    {
                        var list = seriesRo as List<SeriesStream> ?? seriesRo.ToList();
                        await SaveSeriesStreamsBinaryInternalAsync(playlistId, list);
                    }
                    else if (category == "live" && data is IReadOnlyList<LiveStream> liveRo)
                    {
                        var list = liveRo as List<LiveStream> ?? liveRo.ToList();
                        await SaveLiveStreamsBinaryInternalAsync(playlistId, list);
                    }
                }
                finally
                {
                    // [ROOT FIX] Release RefCount
                    if (hasRef) (data as IDisposable)?.Dispose();
                }
            }
            catch (Exception ex) { AppLogger.Error("[Vacuum] FAILED", ex); }
            finally { _diskSemaphore.Release(); }
        }

        public void InvalidateRamSessionsForPlaylist(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId)) return;
            string safeId = GetSafePlaylistId(playlistId);
            string prefix = $"{safeId}_";
            foreach (var key in _streamListsCache.Keys.ToList())
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal) && _streamListsCache.TryRemove(key, out var s))
                    (s as IDisposable)?.Dispose();
            }
        }

        public async Task CheckAndRepairAllCachesAsync(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId)) return;
            string safeId = GetSafePlaylistId(playlistId);
            string[] categories = { "vod", "series", "live_streams" };
            
            foreach (var cat in categories)
            {
                string fileName = $"cache_{safeId}_{cat}.bin";
                await CheckAndRepairFileAsync(fileName);
            }
        }

        private async Task CheckAndRepairFileAsync(string fileName)
        {
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                if (!(await folder.TryGetItemAsync(fileName) is IStorageFile file)) return;

                using var stream = await file.OpenStreamForReadAsync();
                if (stream.Length < 32) { AppLogger.Warn($"[Repair] {fileName}: too small ({stream.Length}), skip."); return; }

                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                int magic = reader.ReadInt32();
                int version = reader.ReadInt32();
                int count = reader.ReadInt32();
                int bufferLen = reader.ReadInt32();
                bool magicOk = magic == 0x564F4444 || magic == 0x53455244 || magic == 0x43415443 || magic == 0x4256494C;
                if (!magicOk || version < 2 || count < 0 || bufferLen < 0)
                {
                    AppLogger.Warn($"[Repair] {fileName}: invalid header (magic=0x{magic:X8}, ver={version}, count={count}).");
                    return;
                }

                if (magic != 0x4256494C)
                {
                    int recordSize = magic == 0x53455244 ? Marshal.SizeOf<Models.Metadata.SeriesRecord>()
                        : magic == 0x564F4444 ? Marshal.SizeOf<Models.Metadata.VodRecord>()
                        : Marshal.SizeOf<Models.Metadata.CategoryRecord>();
                    long stringsOffset = magic == 0x564F4444
                        ? 32L + (count * (long)recordSize) + (count * 8L)
                        : 32L + (count * (long)recordSize);
                    long minLen = stringsOffset + bufferLen;
                    if (stream.Length < minLen)
                    {
                        AppLogger.Warn($"[Repair] {fileName}: layout mismatch (len={stream.Length}, need>={minLen}).");
                        return;
                    }
                }

                stream.Seek(16, SeekOrigin.Begin);
                byte dirtyBit = reader.ReadByte();

                if (dirtyBit == 1)
                {
                    AppLogger.Warn($"[Repair] Unexpected closure for {fileName}. Clearing dirty bit.");
                    using (var randomStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        using (var writeStream = randomStream.AsStreamForWrite())
                        {
                            writeStream.Seek(16, SeekOrigin.Begin);
                            writeStream.WriteByte(0);
                            await writeStream.FlushAsync();
                        }
                    }
                }

            }
            catch { }
            finally { _diskSemaphore.Release(); }
        }
        #endregion


    }

    public class SeriesInfoResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("episodes")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<Dictionary<string, List<SeriesEpisodeDef>>>))]
        public Dictionary<string, List<SeriesEpisodeDef>> Episodes { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<SeriesInfoDetails>))]
        public SeriesInfoDetails Info { get; set; }
    }

    public class SeriesInfoDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("tmdb_id")]
        public object TmdbId { get; set; } // Can be int or string
        
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("cover")]
        public string Cover { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("plot")]
        public string Plot { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("cast")]
        public string Cast { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("genre")]
        public string Genre { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("director")]
        public string Director { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rating")]
        public string Rating { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("backdrop_path")]
        public string[] BackdropPath { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("age")]
        public string Age { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("country")]
        public string Country { get; set; }
    }

    public class SeriesEpisodeDef
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("episode_num")]
        public object EpisodeNum { get; set; } // Can be int or string
        
        [System.Text.Json.Serialization.JsonPropertyName("season")]
        public object Season { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        public SeriesEpisodeInfo Info { get; set; }
    }

    public class SeriesEpisodeInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("movie_image")]
        public string MovieImage { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("plot")]
        public string Plot { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("duration")]
        public string Duration { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("video")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalVideoInfo>))]
        public TechnicalVideoInfo Video { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("audio")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalAudioInfo>))]
        public TechnicalAudioInfo Audio { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bitrate")]
        public object Bitrate { get; set; } // Can be int or string
    }

    public class TechnicalVideoInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("codec_name")]
        public string CodecName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("width")]
        public int Width { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("height")]
        public int Height { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("display_aspect_ratio")]
        public object? AspectRatio { get; set; }
    }

    public class TechnicalAudioInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("codec_name")]
        public string CodecName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("channels")]
        public int Channels { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("channel_layout")]
        public string ChannelLayout { get; set; }
    }

    public class MovieInfoResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("info")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<MovieInfoDetails>))]
        public MovieInfoDetails Info { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("movie_data")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<MovieDataDetails>))]
        public MovieDataDetails MovieData { get; set; }
    }

    public class MovieInfoDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("movie_image")]
        public string MovieImage { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("cover_big")]
        public string CoverBig { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("plot")]
        public string Plot { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("cast")]
        public string Cast { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("genre")]
        public string Genre { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("director")]
        public string Director { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("rating")]
        public object? RatingRaw { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string Rating => RatingRaw?.ToString() ?? "";
        
        [System.Text.Json.Serialization.JsonPropertyName("releasedate")]
        public string ReleaseDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("released")]
        public string Released { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("backdrop_path")]
        public string[] BackdropPath { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("age")]
        public string Age { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mpaa_rating")]
        public string MpaaRating { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("country")]
        public string Country { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("duration")]
        public string Duration { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("video")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalVideoInfo>))]
        public TechnicalVideoInfo Video { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("audio")]
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalAudioInfo>))]
        public TechnicalAudioInfo Audio { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bitrate")]
        public object Bitrate { get; set; }
    }
    
    public class MovieDataDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; }
    }

    // [FIX] Generic converter to handle Xtream Codes API inconsistency (empty array [] instead of object {})
    public class SafeObjectConverter<T> : JsonConverter<T> where T : class
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Skip();
                return null;
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                return JsonSerializer.Deserialize<T>(ref reader, options);
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }

    public class PersistentIptvIndex
    {
        public string VodHash { get; set; }
        public string SeriesHash { get; set; }
        public Dictionary<string, List<StreamIdRef>> ImdbIndex { get; set; }
        public Dictionary<string, List<StreamIdRef>> TokenIndex { get; set; }
    }

    public struct StreamIdRef
    {
        public int Id { get; set; }
        public bool IsSeries { get; set; }
    }
}

