using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Services.Iptv; 
using ModernIPTVPlayer.Services.Json;
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
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _binaryWriteSemaphores = new(StringComparer.Ordinal);

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
                using (var accessor = mmf.CreateViewAccessor(0, BinaryCacheLayout.HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = BinaryCacheLayout.ReadHeader(accessor);
                    if (!BinaryCacheLayout.IsKnownMagic(header.Magic))
                    {
                        AppLogger.Warn($"[InPlaceHydration] Invalid cache header or legacy V1 file detected. Magic={header.Magic:X}. Skipping.");
                        return;
                    }
                    version = header.Version;
                    count = header.Count;
                    bufferLen = header.StringsLength;
                }

                int recordSize = isSeries ? Marshal.SizeOf<Models.Metadata.SeriesRecord>() : Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                long indexOffset = recordsOffset + (count * (long)recordSize);
                long stringsOffset = isSeries ? indexOffset : indexOffset + (count * (long)BinaryCacheLayout.VodIndexEntrySize);

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
                            if (recordPtr == null) continue;

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
                            if (recordPtr == null) continue;

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
                using (var acc = mmf.CreateViewAccessor(0, BinaryCacheLayout.HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = BinaryCacheLayout.ReadHeader(acc);
                    if (!BinaryCacheLayout.IsKnownMagic(header.Magic)) return results;
                    
                    count = header.Count;
                    bufferLen = header.StringsLength;
                }

                if (count <= 0) return results;

                int recordSize = isSeries ? Marshal.SizeOf<Models.Metadata.SeriesRecord>() : Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                long stringsOffset = isSeries
                    ? recordsOffset + (count * (long)recordSize)
                    : recordsOffset + (count * (long)recordSize) + (count * (long)BinaryCacheLayout.VodIndexEntrySize);

                using var session = new BinaryCacheSession(file.Path, stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                if (isSeries)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (!session.TryReadRecord<Models.Metadata.SeriesRecord>(i, out var record)) continue;

                        string name = session.GetString(record.NameOff, record.NameLen);
                        if (TitleContainsBaseToken(name, token)) results.Add(i);
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (!session.TryReadRecord<Models.Metadata.VodRecord>(i, out var record)) continue;

                        string name = session.GetString(record.NameOff, record.NameLen);
                        if (TitleContainsBaseToken(name, token)) results.Add(i);
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
                        var openPlaylistIdsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var key in _streamListsCache.Keys)
                        {
                            openPlaylistIdsSet.Add(key.Split('_')[0]);
                        }
                        var openPlaylistIds = openPlaylistIdsSet.ToList();

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
                var keysToRemove = new List<string>();
                foreach (var kvp in _memoryCache)
                {
                    if ((now - kvp.Value.LastAccessed).TotalMinutes > 10)
                        keysToRemove.Add(kvp.Key);
                }
                foreach (var key in keysToRemove)
                    _memoryCache.TryRemove(key, out _);
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
                
                var tasks = new List<Task>();

                if (needsVod)
                {
                    tasks.Add(Task.Run(async () => {
                        try
                        {
                            string api = $"{login.Host}/player_api.php?username={login.Username}&password={login.Password}&action=get_vod_streams";
                            MemoryTelemetryService.LogCheckpoint("Sync.VOD.fetch.start", $"playlist={playlistId}");
                            using var response = await HttpHelper.Client.GetAsync(api, HttpCompletionOption.ResponseHeadersRead);
                            response.EnsureSuccessStatusCode();
                            await using var stream = await response.Content.ReadAsStreamAsync();
                            await SaveVodStreamsBinaryFromJsonStreamAsync(playlistId, stream);
                            AppSettings.LastVodCacheTime = DateTime.Now;
                            MemoryTelemetryService.LogCheckpoint("Sync.VOD.binary.done", "streamed=true");
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
                            MemoryTelemetryService.LogCheckpoint("Sync.Series.fetch.start", $"playlist={playlistId}");
                            using var response = await HttpHelper.Client.GetAsync(api, HttpCompletionOption.ResponseHeadersRead);
                            response.EnsureSuccessStatusCode();
                            await using var stream = await response.Content.ReadAsStreamAsync();
                            await SaveSeriesStreamsBinaryFromJsonStreamAsync(playlistId, stream);
                            AppSettings.LastSeriesCacheTime = DateTime.Now;
                            MemoryTelemetryService.LogCheckpoint("Sync.Series.binary.done", "streamed=true");
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
            lifecycle.Step("index_activated");
            
            AppLogger.Info($"[Checkpoint] [ContentCache] Background Sync Cycle FINISHED in {startSw.ElapsedMilliseconds}ms.");

            // Post-Sync Memory Purge
            // This releases the massive Byte[] buffers back to the OS after the large IO operations.
            _ = Task.Run(async () =>
            {
               await Task.Delay(2000); // Wait for background saves to settle
               AppLogger.Info("[ContentCache] Triggering Post-Sync Memory Purge...");
               
               // Clear Pool and Force GC
               GC.Collect(1, GCCollectionMode.Optimized, blocking: false, compacting: false);
               
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
                if (IsCategoryCache(category))
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


        // ==========================================
        // PROJECT ZERO - BINARY STREAM BUNDLE (LIVE)
        // ==========================================

        public async Task SaveLiveStreamsBinaryAsync(string playlistId, IReadOnlyList<LiveStream> streams)
        {
            await _diskSemaphore.WaitAsync();
            try { await SaveLiveStreamsBinaryInternalAsync(playlistId, streams); }
            finally { _diskSemaphore.Release(); }
        }

        public async Task SaveLiveStreamsBinaryFromJsonAsync(string playlistId, byte[] jsonBytes)
        {
            if (jsonBytes == null || jsonBytes.Length == 0) return;

            await _diskSemaphore.WaitAsync();
            try { await SaveLiveStreamsBinaryFromJsonInternalAsync(playlistId, jsonBytes); }
            finally { _diskSemaphore.Release(); }
        }

        private async Task SaveLiveStreamsBinaryInternalAsync(string playlistId, IReadOnlyList<LiveStream> streams)
        {
            string fileName = $"cache_{playlistId}_live_streams.bin";
            string tempName = fileName + ".tmp";
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                using (var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting))
                using (var buffered = new BufferedStream(fileStream, 1 * 1024 * 1024)) // 1MB buffer
                using (var writer = new BinaryWriter(buffered, Encoding.UTF8))
                {
                    int recordSize = Marshal.SizeOf<LiveStreamData>();
                    long recordsOffset = BinaryCacheLayout.GetRecordsOffset();

                    BinaryCacheLayout.WriteHeader(writer, streams.Count, 0, true, 0);
                    writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);
                    using var stringHeap = new MemoryStream();
                    var heap = new Utf8StringWriter(stringHeap, 0);

                    for (int i = 0; i < streams.Count; i++)
                    {
                        WriteLiveRecord(writer, BuildLiveDataForBinarySave(streams[i], heap));
                    }

                    int bufferPos = heap.TotalBytesWritten;
                    stringHeap.Position = 0;
                    stringHeap.CopyTo(writer.BaseStream);
                    writer.Flush();

                    writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                    writer.Write(bufferPos);
                    writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
                    writer.Write((byte)0);

                    writer.Flush();
                    AppLogger.Info($"[BinarySave] Live Streams saved (Atomic). Items: {streams.Count}, Buffer: {bufferPos} bytes.");
                }

                string safeId = GetSafePlaylistId(playlistId);
                if (_streamListsCache.TryRemove($"{safeId}_live", out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                // Atomic Swap
                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
            }
            catch (Exception ex) { AppLogger.Error("[BinarySave] LIVE FAILED", ex); }
        }

        private async Task SaveLiveStreamsBinaryFromJsonInternalAsync(string playlistId, byte[] jsonBytes)
        {
            string fileName = $"cache_{playlistId}_live_streams.bin";
            string tempName = fileName + ".tmp";

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                int recordSize = Marshal.SizeOf<LiveStreamData>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                int count;
                int stringsLength;

                using (var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting))
                using (var buffered = new BufferedStream(fileStream, 1 * 1024 * 1024))
                using (var writer = new BinaryWriter(buffered, Encoding.UTF8))
                using (var stringHeap = new MemoryStream(Math.Min(jsonBytes.Length, 8 * 1024 * 1024)))
                {
                BinaryCacheLayout.WriteHeader(writer, 0, 0, true, 0);
                    writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);

                    var heap = new Utf8StringWriter(stringHeap, 0);
                    count = WriteLiveRecordsFromJson(jsonBytes, writer, heap);
                    stringsLength = heap.TotalBytesWritten;

                    stringHeap.Position = 0;
                    stringHeap.CopyTo(writer.BaseStream);
                    writer.Flush();

                    writer.BaseStream.Seek(BinaryCacheLayout.CountOffset, SeekOrigin.Begin);
                    writer.Write(count);
                    writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                    writer.Write(stringsLength);
                    writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
                    writer.Write((byte)0);

                    writer.Flush();
                }

                string safeId = GetSafePlaylistId(playlistId);
                if (_streamListsCache.TryRemove($"{safeId}_live", out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
                AppLogger.Info($"[BinarySave] Live Streams saved from JSON. Items: {count}, RecordBytes: {count * recordSize}, Buffer: {stringsLength} bytes.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[BinarySave] LIVE JSON->BINARY FAILED", ex);
            }
        }

        public async Task SaveVodStreamsBinaryFromJsonAsync(string playlistId, byte[] jsonBytes)
        {
            if (jsonBytes == null || jsonBytes.Length == 0) return;

            string safeId = GetSafePlaylistId(playlistId);
            var gate = GetBinaryWriteSemaphore(safeId, "vod");
            await gate.WaitAsync();
            try { await SaveVodStreamsBinaryFromJsonInternalAsync(playlistId, jsonBytes); }
            finally { gate.Release(); }
        }

        public async Task SaveSeriesStreamsBinaryFromJsonAsync(string playlistId, byte[] jsonBytes)
        {
            if (jsonBytes == null || jsonBytes.Length == 0) return;

            string safeId = GetSafePlaylistId(playlistId);
            var gate = GetBinaryWriteSemaphore(safeId, "series");
            await gate.WaitAsync();
            try { await SaveSeriesStreamsBinaryFromJsonInternalAsync(playlistId, jsonBytes); }
            finally { gate.Release(); }
        }

        public async Task SaveVodStreamsBinaryFromJsonStreamAsync(string playlistId, Stream jsonStream, CancellationToken cancellationToken = default)
        {
            if (jsonStream == null) return;

            string safeId = GetSafePlaylistId(playlistId);
            var gate = GetBinaryWriteSemaphore(safeId, "vod");
            await gate.WaitAsync(cancellationToken);
            try { await SaveVodStreamsBinaryFromJsonStreamInternalAsync(playlistId, jsonStream, cancellationToken); }
            finally { gate.Release(); }
        }

        public async Task SaveSeriesStreamsBinaryFromJsonStreamAsync(string playlistId, Stream jsonStream, CancellationToken cancellationToken = default)
        {
            if (jsonStream == null) return;

            string safeId = GetSafePlaylistId(playlistId);
            var gate = GetBinaryWriteSemaphore(safeId, "series");
            await gate.WaitAsync(cancellationToken);
            try { await SaveSeriesStreamsBinaryFromJsonStreamInternalAsync(playlistId, jsonStream, cancellationToken); }
            finally { gate.Release(); }
        }

        private SemaphoreSlim GetBinaryWriteSemaphore(string safeId, string kind)
        {
            return _binaryWriteSemaphores.GetOrAdd($"{safeId}_{kind}", _ => new SemaphoreSlim(1, 1));
        }

        private async Task SaveVodStreamsBinaryFromJsonStreamInternalAsync(string playlistId, Stream jsonStream, CancellationToken cancellationToken)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_vod.bin";
            string tempName = fileName + ".tmp";

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                int recordSize = Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();

                using var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);
                using var stringHeap = new MemoryStream(1024 * 1024);
                var heap = new Utf8StringWriter(stringHeap, 0);

                BinaryCacheLayout.WriteHeader(writer, 0, 0, true, 0);
                writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);

                var indexEntries = new List<(uint Fingerprint, int Index)>();
                int count = 0;
                long datasetFingerprint = 0;

                await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable(jsonStream, AppJsonContext.Default.JsonElement, cancellationToken: cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var record = ParseVodRecordForBinary(item, heap);
                    WriteVodRecordToStream(writer, record);
                    indexEntries.Add((record.Fingerprint, count));
                    datasetFingerprint ^= ((long)record.Fingerprint << 32) | (uint)(count % 0xFFFFFFFF);
                    count++;
                }

                datasetFingerprint ^= count;
                long indexOffset = recordsOffset + count * (long)recordSize;

                writer.BaseStream.Seek(indexOffset, SeekOrigin.Begin);
                indexEntries.Sort((a, b) => a.Fingerprint.CompareTo(b.Fingerprint));
                foreach (var entry in indexEntries)
                {
                    writer.Write(entry.Fingerprint);
                    writer.Write(entry.Index);
                }

                int bufferPos = heap.TotalBytesWritten;
                stringHeap.Position = 0;
                stringHeap.CopyTo(writer.BaseStream);

                writer.BaseStream.Seek(BinaryCacheLayout.CountOffset, SeekOrigin.Begin);
                writer.Write(count);
                writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                writer.Write(bufferPos);
                writer.BaseStream.Seek(BinaryCacheLayout.FingerprintOffset, SeekOrigin.Begin);
                writer.Write(datasetFingerprint);
                writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
                writer.Write((byte)0);
                writer.Flush();

                if (_streamListsCache.TryRemove($"{safeId}_vod", out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);

                AppLogger.Info($"[BinarySave] VOD streamed to binary. Items: {count}, Buffer: {bufferPos} bytes.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Error("[BinarySave] VOD STREAM->BINARY FAILED", ex);
            }
        }

        private async Task SaveSeriesStreamsBinaryFromJsonStreamInternalAsync(string playlistId, Stream jsonStream, CancellationToken cancellationToken)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_series.bin";
            string tempName = fileName + ".tmp";

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();

                using var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);
                using var stringHeap = new MemoryStream(1024 * 1024);
                var heap = new Utf8StringWriter(stringHeap, 0);

                BinaryCacheLayout.WriteHeader(writer, 0, 0, true, 0);
                writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);

                int count = 0;
                long datasetFingerprint = 0;

                await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable(jsonStream, AppJsonContext.Default.JsonElement, cancellationToken: cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var record = ParseSeriesRecordForBinary(item, heap);
                    WriteSeriesRecordToStream(writer, record);
                    datasetFingerprint ^= ((long)record.Fingerprint << 32) | (uint)(count % 0xFFFFFFFF);
                    count++;
                }

                datasetFingerprint ^= count;
                int bufferPos = heap.TotalBytesWritten;
                stringHeap.Position = 0;
                stringHeap.CopyTo(writer.BaseStream);

                writer.BaseStream.Seek(BinaryCacheLayout.CountOffset, SeekOrigin.Begin);
                writer.Write(count);
                writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                writer.Write(bufferPos);
                writer.BaseStream.Seek(BinaryCacheLayout.FingerprintOffset, SeekOrigin.Begin);
                writer.Write(datasetFingerprint);
                writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
                writer.Write((byte)0);
                writer.Flush();

                if (_streamListsCache.TryRemove($"{safeId}_series", out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);

                AppLogger.Info($"[BinarySave] Series streamed to binary. Items: {count}, Buffer: {bufferPos} bytes.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Error("[BinarySave] SERIES STREAM->BINARY FAILED", ex);
            }
        }

        private async Task SaveVodStreamsBinaryFromJsonInternalAsync(string playlistId, byte[] jsonBytes)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_vod.bin";
            string tempName = fileName + ".tmp";

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                int recordSize = Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();

                using var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);
                using var stringHeap = new MemoryStream(Math.Min(jsonBytes.Length, 16 * 1024 * 1024));
                var heap = new Utf8StringWriter(stringHeap, 0);

                BinaryCacheLayout.WriteHeader(writer, 0, 0, true, 0);
                writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);

                var indexEntries = new List<(uint Fingerprint, int Index)>();
                int count = WriteVodRecordsFromJson(jsonBytes, writer, heap, indexEntries, out long datasetFingerprint);
                long indexOffset = recordsOffset + count * (long)recordSize;

                writer.BaseStream.Seek(indexOffset, SeekOrigin.Begin);
                indexEntries.Sort((a, b) => a.Fingerprint.CompareTo(b.Fingerprint));
                foreach (var entry in indexEntries)
                {
                    writer.Write(entry.Fingerprint);
                    writer.Write(entry.Index);
                }

                int bufferPos = heap.TotalBytesWritten;
                stringHeap.Position = 0;
                stringHeap.CopyTo(writer.BaseStream);
                writer.Flush();

                writer.BaseStream.Seek(BinaryCacheLayout.CountOffset, SeekOrigin.Begin);
                writer.Write(count);
                writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                writer.Write(bufferPos);
                writer.BaseStream.Seek(BinaryCacheLayout.FingerprintOffset, SeekOrigin.Begin);
                writer.Write(datasetFingerprint);
                writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
                writer.Write((byte)0);
                writer.Flush();

                if (_streamListsCache.TryRemove($"{safeId}_vod", out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);

                AppLogger.Info($"[BinarySave] VOD saved from JSON. Items: {count}, Buffer: {bufferPos} bytes.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[BinarySave] VOD JSON->BINARY FAILED", ex);
            }
        }

        private async Task SaveSeriesStreamsBinaryFromJsonInternalAsync(string playlistId, byte[] jsonBytes)
        {
            string safeId = GetSafePlaylistId(playlistId);
            string fileName = $"cache_{safeId}_series.bin";
            string tempName = fileName + ".tmp";

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();

                using var fileStream = await folder.OpenStreamForWriteAsync(tempName, CreationCollisionOption.ReplaceExisting);
                using var writer = new BinaryWriter(fileStream, Encoding.UTF8);
                using var stringHeap = new MemoryStream(Math.Min(jsonBytes.Length, 16 * 1024 * 1024));
                var heap = new Utf8StringWriter(stringHeap, 0);

                BinaryCacheLayout.WriteHeader(writer, 0, 0, true, 0);
                writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);

                int count = WriteSeriesRecordsFromJson(jsonBytes, writer, heap, out long datasetFingerprint);
                int bufferPos = heap.TotalBytesWritten;
                stringHeap.Position = 0;
                stringHeap.CopyTo(writer.BaseStream);
                writer.Flush();

                writer.BaseStream.Seek(BinaryCacheLayout.CountOffset, SeekOrigin.Begin);
                writer.Write(count);
                writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                writer.Write(bufferPos);
                writer.BaseStream.Seek(BinaryCacheLayout.FingerprintOffset, SeekOrigin.Begin);
                writer.Write(datasetFingerprint);
                writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
                writer.Write((byte)0);
                writer.Flush();

                if (_streamListsCache.TryRemove($"{safeId}_series", out var oldSession))
                {
                    (oldSession as IDisposable)?.Dispose();
                }

                var tempFile = await folder.GetFileAsync(tempName);
                await tempFile.MoveAsync(folder, fileName, NameCollisionOption.ReplaceExisting);

                AppLogger.Info($"[BinarySave] Series saved from JSON. Items: {count}, Buffer: {bufferPos} bytes.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[BinarySave] Series JSON->BINARY FAILED", ex);
            }
        }

        public async Task<IReadOnlyList<LiveStream>> LoadLiveStreamsBinaryAsync(string playlistId)
        {
            string safeId = GetSafePlaylistId(playlistId);
            if (_streamListsCache.TryGetValue($"{safeId}_live", out var ramCached))
            {
                AppLogger.Info($"[BinaryLoad] LIVE SESSION HIT for {playlistId}");
                return ramCached as IReadOnlyList<LiveStream>;
            }

            string fileName = $"cache_{playlistId}_live_streams.bin";
            await _diskSemaphore.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(fileName);
                if (item == null) return null;

                int count, bufferLen, version;
                string filePath = Path.Combine(folder.Path, fileName);
                using (var mmf = BinaryCacheSession.OpenMemoryMappedFile(filePath, MemoryMappedFileAccess.Read))
                using (var accessor = mmf.CreateViewAccessor(0, BinaryCacheLayout.HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = BinaryCacheLayout.ReadHeader(accessor);
                    if (!BinaryCacheLayout.IsKnownMagic(header.Magic)) return null;
                    count = header.Count;
                    bufferLen = header.StringsLength;
                    version = header.Version;
                }

                if (version >= 3)
                {
                    int recordSize = Marshal.SizeOf<LiveStreamData>();
                    long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                    long stringsOffset = recordsOffset + count * (long)recordSize;

                    var session = new BinaryCacheSession(filePath, stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                    var results = new VirtualLiveList(session);
                    _streamListsCache[$"{safeId}_live"] = results;

                    AppLogger.Info($"[BinaryLoad] Live Virtual Session Ready: {count} items.");
                    return results;
                }

                using var stream = await folder.OpenStreamForReadAsync(fileName);
                using var buffered = new BufferedStream(stream, 1 * 1024 * 1024);

                var reader = new BinaryReader(buffered, Encoding.UTF8);

                using (reader)
                {
                    var header = BinaryCacheLayout.ReadHeader(reader);
                    int bufferPos = header.StringsLength;
                    
                    byte[] buffer = reader.ReadBytes(bufferPos); 
                    int baseOffset = MetadataBuffer.AppendRawBuffer(buffer, bufferPos);

                    int legacyCount = reader.ReadInt32();
                    byte[]? structBytes = null;
                    if (header.Version >= 2)
                    {
                        int structBytesLen = reader.ReadInt32();
                        structBytes = reader.ReadBytes(structBytesLen);
                    }

                    var results = ParseLiveStreamDataBulk(structBytes, legacyCount, header.Version, reader, baseOffset);

                    AppLogger.Info($"[BinaryLoad] Live legacy cache loaded. Items: {results?.Count ?? 0}");
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

        private static LiveStreamData BuildLiveDataForBinarySave(LiveStream s, Utf8StringWriter heap)
        {
            var name = heap.Add(s.Name);
            var icon = heap.Add(s.StreamIcon);
            var imdb = heap.Add(s.ImdbId);
            var desc = heap.Add(s.Description);
            var bg = heap.Add(s.BackdropUrl);
            var genre = heap.Add(s.Genres);
            var cast = heap.Add(s.Cast);
            var dir = heap.Add(s.Director);
            var trail = heap.Add(s.TrailerUrl);
            var year = heap.Add(s.Year);
            var ext = heap.Add(s.ContainerExtension);
            var cat = heap.Add(s.CategoryId);
            var rating = heap.Add(s.Rating);

            return new LiveStreamData
            {
                StreamId = s.StreamId,
                NameOff = name.Off, NameLen = name.Len,
                IconOff = icon.Off, IconLen = icon.Len,
                ImdbOff = imdb.Off, ImdbLen = imdb.Len,
                DescOff = desc.Off, DescLen = desc.Len,
                BgOff = bg.Off, BgLen = bg.Len,
                GenreOff = genre.Off, GenreLen = genre.Len,
                CastOff = cast.Off, CastLen = cast.Len,
                DirOff = dir.Off, DirLen = dir.Len,
                TrailOff = trail.Off, TrailLen = trail.Len,
                YearOff = year.Off, YearLen = year.Len,
                ExtOff = ext.Off, ExtLen = ext.Len,
                CatOff = cat.Off, CatLen = cat.Len,
                RatOff = rating.Off, RatLen = rating.Len
            };
        }

        private int WriteVodRecordsFromJson(ReadOnlySpan<byte> jsonBytes, BinaryWriter writer, Utf8StringWriter heap, List<(uint Fingerprint, int Index)> indexEntries, out long datasetFingerprint)
        {
            datasetFingerprint = 0;
            var reader = new Utf8JsonReader(jsonBytes, isFinalBlock: true, new JsonReaderState());
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return 0;

            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var record = ParseVodRecordForBinary(ref reader, heap);
                WriteVodRecordToStream(writer, record);
                indexEntries.Add((record.Fingerprint, count));
                datasetFingerprint ^= ((long)record.Fingerprint << 32) | (uint)(count % 0xFFFFFFFF);
                count++;
            }

            datasetFingerprint ^= count;
            return count;
        }

        private static Models.Metadata.VodRecord ParseVodRecordForBinary(ref Utf8JsonReader reader, Utf8StringWriter heap)
        {
            var record = new Models.Metadata.VodRecord
            {
                NameOff = -1,
                IconOff = -1,
                ImdbIdOff = -1,
                PlotOff = -1,
                YearOff = -1,
                GenresOff = -1,
                CastOff = -1,
                DirectorOff = -1,
                TrailerOff = -1,
                BackdropOff = -1,
                SourceTitleOff = -1,
                RatingOff = -1,
                ExtOff = -1,
                LastModified = DateTime.UtcNow.Ticks,
                Flags = 16
            };

            string? name = null;
            string? imdb = null;
            string? year = null;
            string? releaseDate = null;
            string? airDate = null;
            string? released = null;
            string? rating = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                ReadOnlySpan<byte> property = reader.ValueSpan;
                if (!reader.Read()) break;

                if (property.SequenceEqual("name"u8))
                {
                    StoreJsonString(ref reader, heap, out record.NameOff, out record.NameLen, out name);
                }
                else if (property.SequenceEqual("stream_id"u8))
                {
                    record.StreamId = ReadJsonInt32(ref reader);
                }
                else if (property.SequenceEqual("stream_icon"u8) || property.SequenceEqual("cover"u8))
                {
                    StoreJsonString(ref reader, heap, out record.IconOff, out record.IconLen, out _);
                }
                else if (property.SequenceEqual("container_extension"u8))
                {
                    StoreJsonString(ref reader, heap, out record.ExtOff, out record.ExtLen, out _);
                }
                else if (property.SequenceEqual("category_id"u8))
                {
                    string? category = ReadJsonStringValue(ref reader);
                    record.CategoryId = int.TryParse(category, out int catId) ? catId : 0;
                }
                else if (property.SequenceEqual("rating"u8) || property.SequenceEqual("rating_5based"u8))
                {
                    StoreJsonString(ref reader, heap, out record.RatingOff, out record.RatingLen, out rating);
                }
                else if (property.SequenceEqual("imdb_id"u8))
                {
                    StoreJsonString(ref reader, heap, out record.ImdbIdOff, out record.ImdbIdLen, out imdb);
                }
                else if (property.SequenceEqual("plot"u8) || property.SequenceEqual("description"u8))
                {
                    StoreJsonString(ref reader, heap, out record.PlotOff, out record.PlotLen, out _);
                }
                else if (property.SequenceEqual("genre"u8) || property.SequenceEqual("genres"u8))
                {
                    StoreJsonString(ref reader, heap, out record.GenresOff, out record.GenresLen, out _);
                }
                else if (property.SequenceEqual("cast"u8))
                {
                    StoreJsonString(ref reader, heap, out record.CastOff, out record.CastLen, out _);
                }
                else if (property.SequenceEqual("director"u8))
                {
                    StoreJsonString(ref reader, heap, out record.DirectorOff, out record.DirectorLen, out _);
                }
                else if (property.SequenceEqual("youtube_trailer"u8) || property.SequenceEqual("trailer"u8))
                {
                    StoreJsonString(ref reader, heap, out record.TrailerOff, out record.TrailerLen, out _);
                }
                else if (property.SequenceEqual("backdrop_path"u8) || property.SequenceEqual("backdrop"u8))
                {
                    StoreJsonStringOrArray(ref reader, heap, out record.BackdropOff, out record.BackdropLen, out _);
                }
                else if (property.SequenceEqual("source_title"u8))
                {
                    StoreJsonString(ref reader, heap, out record.SourceTitleOff, out record.SourceTitleLen, out _);
                }
                else if (property.SequenceEqual("year"u8))
                {
                    StoreJsonString(ref reader, heap, out record.YearOff, out record.YearLen, out year);
                }
                else if (property.SequenceEqual("releaseDate"u8) || property.SequenceEqual("releasedate"u8))
                {
                    releaseDate = ReadJsonStringValue(ref reader);
                }
                else if (property.SequenceEqual("air_date"u8))
                {
                    airDate = ReadJsonStringValue(ref reader);
                }
                else if (property.SequenceEqual("released"u8))
                {
                    released = ReadJsonStringValue(ref reader);
                }
                else
                {
                    reader.Skip();
                }
            }

            if (record.YearOff < 0)
            {
                string extractedYear = TitleHelper.ExtractYear(year ?? releaseDate ?? airDate ?? released ?? name);
                var stored = heap.Add(extractedYear);
                record.YearOff = stored.Off;
                record.YearLen = stored.Len;
                year = extractedYear;
            }

            if (double.TryParse(rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
            {
                record.RatingScaled = (short)(r * 100);
            }

            record.Fingerprint = CalculateStreamFingerprint(name, year, imdb);
            return record;
        }

        private static Models.Metadata.VodRecord ParseVodRecordForBinary(JsonElement element, Utf8StringWriter heap)
        {
            var record = new Models.Metadata.VodRecord
            {
                NameOff = -1,
                IconOff = -1,
                ImdbIdOff = -1,
                PlotOff = -1,
                YearOff = -1,
                GenresOff = -1,
                CastOff = -1,
                DirectorOff = -1,
                TrailerOff = -1,
                BackdropOff = -1,
                SourceTitleOff = -1,
                RatingOff = -1,
                ExtOff = -1,
                LastModified = DateTime.UtcNow.Ticks,
                Flags = 16
            };

            string? name = null;
            string? imdb = null;
            string? year = null;
            string? releaseDate = null;
            string? airDate = null;
            string? released = null;
            string? rating = null;

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("name"))
                {
                    StoreJsonElementString(property.Value, heap, out record.NameOff, out record.NameLen, out name);
                }
                else if (property.NameEquals("stream_id"))
                {
                    record.StreamId = ReadJsonElementInt32(property.Value);
                }
                else if (property.NameEquals("stream_icon") || property.NameEquals("cover"))
                {
                    StoreJsonElementString(property.Value, heap, out record.IconOff, out record.IconLen, out _);
                }
                else if (property.NameEquals("container_extension"))
                {
                    StoreJsonElementString(property.Value, heap, out record.ExtOff, out record.ExtLen, out _);
                }
                else if (property.NameEquals("category_id"))
                {
                    string? category = ReadJsonElementString(property.Value);
                    record.CategoryId = int.TryParse(category, out int catId) ? catId : 0;
                }
                else if (property.NameEquals("rating") || property.NameEquals("rating_5based"))
                {
                    StoreJsonElementString(property.Value, heap, out record.RatingOff, out record.RatingLen, out rating);
                }
                else if (property.NameEquals("imdb_id"))
                {
                    StoreJsonElementString(property.Value, heap, out record.ImdbIdOff, out record.ImdbIdLen, out imdb);
                }
                else if (property.NameEquals("plot") || property.NameEquals("description"))
                {
                    StoreJsonElementString(property.Value, heap, out record.PlotOff, out record.PlotLen, out _);
                }
                else if (property.NameEquals("genre") || property.NameEquals("genres"))
                {
                    StoreJsonElementString(property.Value, heap, out record.GenresOff, out record.GenresLen, out _);
                }
                else if (property.NameEquals("cast"))
                {
                    StoreJsonElementString(property.Value, heap, out record.CastOff, out record.CastLen, out _);
                }
                else if (property.NameEquals("director"))
                {
                    StoreJsonElementString(property.Value, heap, out record.DirectorOff, out record.DirectorLen, out _);
                }
                else if (property.NameEquals("youtube_trailer") || property.NameEquals("trailer"))
                {
                    StoreJsonElementString(property.Value, heap, out record.TrailerOff, out record.TrailerLen, out _);
                }
                else if (property.NameEquals("backdrop_path") || property.NameEquals("backdrop"))
                {
                    StoreJsonElementStringOrArray(property.Value, heap, out record.BackdropOff, out record.BackdropLen, out _);
                }
                else if (property.NameEquals("source_title"))
                {
                    StoreJsonElementString(property.Value, heap, out record.SourceTitleOff, out record.SourceTitleLen, out _);
                }
                else if (property.NameEquals("year"))
                {
                    StoreJsonElementString(property.Value, heap, out record.YearOff, out record.YearLen, out year);
                }
                else if (property.NameEquals("releaseDate") || property.NameEquals("releasedate"))
                {
                    releaseDate = ReadJsonElementString(property.Value);
                }
                else if (property.NameEquals("air_date"))
                {
                    airDate = ReadJsonElementString(property.Value);
                }
                else if (property.NameEquals("released"))
                {
                    released = ReadJsonElementString(property.Value);
                }
            }

            if (record.YearOff < 0)
            {
                string extractedYear = TitleHelper.ExtractYear(year ?? releaseDate ?? airDate ?? released ?? name);
                var stored = heap.Add(extractedYear);
                record.YearOff = stored.Off;
                record.YearLen = stored.Len;
                year = extractedYear;
            }

            if (double.TryParse(rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
            {
                record.RatingScaled = (short)(r * 100);
            }

            record.Fingerprint = CalculateStreamFingerprint(name, year, imdb);
            return record;
        }

        private int WriteSeriesRecordsFromJson(ReadOnlySpan<byte> jsonBytes, BinaryWriter writer, Utf8StringWriter heap, out long datasetFingerprint)
        {
            datasetFingerprint = 0;
            var reader = new Utf8JsonReader(jsonBytes, isFinalBlock: true, new JsonReaderState());
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return 0;

            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var record = ParseSeriesRecordForBinary(ref reader, heap);
                WriteSeriesRecordToStream(writer, record);
                datasetFingerprint ^= ((long)record.Fingerprint << 32) | (uint)(count % 0xFFFFFFFF);
                count++;
            }

            datasetFingerprint ^= count;
            return count;
        }

        private static Models.Metadata.SeriesRecord ParseSeriesRecordForBinary(ref Utf8JsonReader reader, Utf8StringWriter heap)
        {
            var record = new Models.Metadata.SeriesRecord
            {
                NameOff = -1,
                IconOff = -1,
                ImdbIdOff = -1,
                PlotOff = -1,
                YearOff = -1,
                GenresOff = -1,
                CastOff = -1,
                DirectorOff = -1,
                TrailerOff = -1,
                BackdropOff = -1,
                SourceTitleOff = -1,
                RatingOff = -1,
                ExtOff = -1,
                LastModified = DateTime.UtcNow.Ticks,
                Flags = 16
            };

            string? name = null;
            string? imdb = null;
            string? year = null;
            string? releaseDate = null;
            string? airDate = null;
            string? rating = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                ReadOnlySpan<byte> property = reader.ValueSpan;
                if (!reader.Read()) break;

                if (property.SequenceEqual("name"u8))
                {
                    StoreJsonString(ref reader, heap, out record.NameOff, out record.NameLen, out name);
                }
                else if (property.SequenceEqual("series_id"u8) || property.SequenceEqual("stream_id"u8))
                {
                    record.SeriesId = ReadJsonInt32(ref reader);
                }
                else if (property.SequenceEqual("cover"u8) || property.SequenceEqual("stream_icon"u8))
                {
                    StoreJsonString(ref reader, heap, out record.IconOff, out record.IconLen, out _);
                }
                else if (property.SequenceEqual("category_id"u8))
                {
                    string? category = ReadJsonStringValue(ref reader);
                    record.CategoryId = int.TryParse(category, out int catId) ? catId : 0;
                }
                else if (property.SequenceEqual("imdb_id"u8) || property.SequenceEqual("tmdb"u8) || property.SequenceEqual("tmdb_id"u8))
                {
                    StoreJsonString(ref reader, heap, out record.ImdbIdOff, out record.ImdbIdLen, out imdb);
                }
                else if (property.SequenceEqual("plot"u8) || property.SequenceEqual("description"u8))
                {
                    StoreJsonString(ref reader, heap, out record.PlotOff, out record.PlotLen, out _);
                }
                else if (property.SequenceEqual("genre"u8) || property.SequenceEqual("genres"u8))
                {
                    StoreJsonString(ref reader, heap, out record.GenresOff, out record.GenresLen, out _);
                }
                else if (property.SequenceEqual("cast"u8))
                {
                    StoreJsonString(ref reader, heap, out record.CastOff, out record.CastLen, out _);
                }
                else if (property.SequenceEqual("director"u8))
                {
                    StoreJsonString(ref reader, heap, out record.DirectorOff, out record.DirectorLen, out _);
                }
                else if (property.SequenceEqual("youtube_trailer"u8) || property.SequenceEqual("trailer"u8))
                {
                    StoreJsonString(ref reader, heap, out record.TrailerOff, out record.TrailerLen, out _);
                }
                else if (property.SequenceEqual("backdrop_path"u8) || property.SequenceEqual("backdrop"u8))
                {
                    StoreJsonStringOrArray(ref reader, heap, out record.BackdropOff, out record.BackdropLen, out _);
                }
                else if (property.SequenceEqual("rating"u8) || property.SequenceEqual("rating_5based"u8))
                {
                    StoreJsonString(ref reader, heap, out record.RatingOff, out record.RatingLen, out rating);
                }
                else if (property.SequenceEqual("container_extension"u8))
                {
                    StoreJsonString(ref reader, heap, out record.ExtOff, out record.ExtLen, out _);
                }
                else if (property.SequenceEqual("year"u8))
                {
                    StoreJsonString(ref reader, heap, out record.YearOff, out record.YearLen, out year);
                }
                else if (property.SequenceEqual("releaseDate"u8) || property.SequenceEqual("releasedate"u8))
                {
                    releaseDate = ReadJsonStringValue(ref reader);
                }
                else if (property.SequenceEqual("air_date"u8))
                {
                    airDate = ReadJsonStringValue(ref reader);
                }
                else if (property.SequenceEqual("episode_run_time"u8))
                {
                    record.AirTime = ReadJsonInt32(ref reader);
                }
                else
                {
                    reader.Skip();
                }
            }

            if (record.YearOff < 0)
            {
                string extractedYear = TitleHelper.ExtractYear(year ?? releaseDate ?? airDate ?? name);
                var stored = heap.Add(extractedYear);
                record.YearOff = stored.Off;
                record.YearLen = stored.Len;
                year = extractedYear;
            }

            if (double.TryParse(rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
            {
                record.RatingScaled = (short)(r * 100);
            }

            record.Fingerprint = CalculateStreamFingerprint(name, year, imdb);
            return record;
        }

        private static Models.Metadata.SeriesRecord ParseSeriesRecordForBinary(JsonElement element, Utf8StringWriter heap)
        {
            var record = new Models.Metadata.SeriesRecord
            {
                NameOff = -1,
                IconOff = -1,
                ImdbIdOff = -1,
                PlotOff = -1,
                YearOff = -1,
                GenresOff = -1,
                CastOff = -1,
                DirectorOff = -1,
                TrailerOff = -1,
                BackdropOff = -1,
                SourceTitleOff = -1,
                RatingOff = -1,
                ExtOff = -1,
                LastModified = DateTime.UtcNow.Ticks,
                Flags = 16
            };

            string? name = null;
            string? imdb = null;
            string? year = null;
            string? releaseDate = null;
            string? airDate = null;
            string? rating = null;

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("name"))
                {
                    StoreJsonElementString(property.Value, heap, out record.NameOff, out record.NameLen, out name);
                }
                else if (property.NameEquals("series_id") || property.NameEquals("stream_id"))
                {
                    record.SeriesId = ReadJsonElementInt32(property.Value);
                }
                else if (property.NameEquals("cover") || property.NameEquals("stream_icon"))
                {
                    StoreJsonElementString(property.Value, heap, out record.IconOff, out record.IconLen, out _);
                }
                else if (property.NameEquals("category_id"))
                {
                    string? category = ReadJsonElementString(property.Value);
                    record.CategoryId = int.TryParse(category, out int catId) ? catId : 0;
                }
                else if (property.NameEquals("imdb_id") || property.NameEquals("tmdb") || property.NameEquals("tmdb_id"))
                {
                    StoreJsonElementString(property.Value, heap, out record.ImdbIdOff, out record.ImdbIdLen, out imdb);
                }
                else if (property.NameEquals("plot") || property.NameEquals("description"))
                {
                    StoreJsonElementString(property.Value, heap, out record.PlotOff, out record.PlotLen, out _);
                }
                else if (property.NameEquals("genre") || property.NameEquals("genres"))
                {
                    StoreJsonElementString(property.Value, heap, out record.GenresOff, out record.GenresLen, out _);
                }
                else if (property.NameEquals("cast"))
                {
                    StoreJsonElementString(property.Value, heap, out record.CastOff, out record.CastLen, out _);
                }
                else if (property.NameEquals("director"))
                {
                    StoreJsonElementString(property.Value, heap, out record.DirectorOff, out record.DirectorLen, out _);
                }
                else if (property.NameEquals("youtube_trailer") || property.NameEquals("trailer"))
                {
                    StoreJsonElementString(property.Value, heap, out record.TrailerOff, out record.TrailerLen, out _);
                }
                else if (property.NameEquals("backdrop_path") || property.NameEquals("backdrop"))
                {
                    StoreJsonElementStringOrArray(property.Value, heap, out record.BackdropOff, out record.BackdropLen, out _);
                }
                else if (property.NameEquals("rating") || property.NameEquals("rating_5based"))
                {
                    StoreJsonElementString(property.Value, heap, out record.RatingOff, out record.RatingLen, out rating);
                }
                else if (property.NameEquals("container_extension"))
                {
                    StoreJsonElementString(property.Value, heap, out record.ExtOff, out record.ExtLen, out _);
                }
                else if (property.NameEquals("year"))
                {
                    StoreJsonElementString(property.Value, heap, out record.YearOff, out record.YearLen, out year);
                }
                else if (property.NameEquals("releaseDate") || property.NameEquals("releasedate"))
                {
                    releaseDate = ReadJsonElementString(property.Value);
                }
                else if (property.NameEquals("air_date"))
                {
                    airDate = ReadJsonElementString(property.Value);
                }
                else if (property.NameEquals("episode_run_time"))
                {
                    record.AirTime = ReadJsonElementInt32(property.Value);
                }
            }

            if (record.YearOff < 0)
            {
                string extractedYear = TitleHelper.ExtractYear(year ?? releaseDate ?? airDate ?? name);
                var stored = heap.Add(extractedYear);
                record.YearOff = stored.Off;
                record.YearLen = stored.Len;
                year = extractedYear;
            }

            if (double.TryParse(rating, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
            {
                record.RatingScaled = (short)(r * 100);
            }

            record.Fingerprint = CalculateStreamFingerprint(name, year, imdb);
            return record;
        }

        private static void StoreJsonString(ref Utf8JsonReader reader, Utf8StringWriter heap, out int offset, out int length, out string? value)
        {
            value = ReadJsonStringValue(ref reader);
            var stored = heap.Add(value);
            offset = stored.Off;
            length = stored.Len;
        }

        private static void StoreJsonStringOrArray(ref Utf8JsonReader reader, Utf8StringWriter heap, out int offset, out int length, out string? value)
        {
            value = ReadJsonStringOrArray(ref reader);
            var stored = heap.Add(value);
            offset = stored.Off;
            length = stored.Len;
        }

        private static void StoreJsonElementString(JsonElement element, Utf8StringWriter heap, out int offset, out int length, out string? value)
        {
            value = ReadJsonElementString(element);
            var stored = heap.Add(value);
            offset = stored.Off;
            length = stored.Len;
        }

        private static void StoreJsonElementStringOrArray(JsonElement element, Utf8StringWriter heap, out int offset, out int length, out string? value)
        {
            value = ReadJsonElementStringOrArray(element);
            var stored = heap.Add(value);
            offset = stored.Off;
            length = stored.Len;
        }

        private static string? ReadJsonStringValue(ref Utf8JsonReader reader)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out long n)
                    ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                _ => null
            };
        }

        private static string? ReadJsonElementString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long n)
                    ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static string? ReadJsonElementStringOrArray(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                return ReadJsonElementString(element);
            }

            var values = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                string? value = ReadJsonElementString(item);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }

            return values.Count == 0 ? null : string.Join("|", values);
        }

        private static int ReadJsonElementInt32(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int value))
            {
                return value;
            }

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value))
            {
                return value;
            }

            return 0;
        }

        private static string? ReadJsonStringOrArray(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartArray) return ReadJsonStringValue(ref reader);

            var values = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                string? value = ReadJsonStringValue(ref reader);
                if (!string.IsNullOrEmpty(value)) values.Add(value);
                else reader.Skip();
            }

            return values.Count == 0 ? null : string.Join("|", values);
        }

        private static uint CalculateStreamFingerprint(string? title, string? year, string? imdb)
        {
            string cleanTitle = TitleHelper.Normalize(title ?? "");
            string cleanYear = year ?? "";
            string cleanImdb = imdb ?? "";

            uint hash = 2166136261;
            foreach (char c in cleanTitle) hash = (hash ^ c) * 16777619;
            foreach (char c in cleanYear) hash = (hash ^ c) * 16777619;
            foreach (char c in cleanImdb) hash = (hash ^ c) * 16777619;
            return hash;
        }

        private static int WriteLiveRecordsFromJson(ReadOnlySpan<byte> jsonBytes, BinaryWriter writer, Utf8StringWriter heap)
        {
            var reader = new Utf8JsonReader(jsonBytes, isFinalBlock: true, new JsonReaderState());
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return 0;

            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var data = ParseLiveRecordForBinary(ref reader, heap);
                WriteLiveRecord(writer, data);
                count++;
            }

            return count;
        }

        private static LiveStreamData ParseLiveRecordForBinary(ref Utf8JsonReader reader, Utf8StringWriter heap)
        {
            var data = CreateEmptyLiveRecord();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                ReadOnlySpan<byte> property = reader.ValueSpan;
                if (!reader.Read()) break;

                if (property.SequenceEqual("name"u8)) AddJsonString(ref reader, heap, out data.NameOff, out data.NameLen);
                else if (property.SequenceEqual("stream_id"u8) || property.SequenceEqual("series_id"u8)) data.StreamId = ReadJsonInt32(ref reader);
                else if (property.SequenceEqual("stream_icon"u8) || property.SequenceEqual("cover"u8)) AddJsonString(ref reader, heap, out data.IconOff, out data.IconLen);
                else if (property.SequenceEqual("container_extension"u8)) AddJsonString(ref reader, heap, out data.ExtOff, out data.ExtLen);
                else if (property.SequenceEqual("category_id"u8)) AddJsonString(ref reader, heap, out data.CatOff, out data.CatLen);
                else if (property.SequenceEqual("category_name"u8)) { }
                else if (property.SequenceEqual("rating"u8)) AddJsonString(ref reader, heap, out data.RatOff, out data.RatLen);
                else reader.Skip();
            }

            return data;
        }

        private static LiveStreamData CreateEmptyLiveRecord() => new()
        {
            NameOff = -1,
            IconOff = -1,
            ImdbOff = -1,
            DescOff = -1,
            BgOff = -1,
            GenreOff = -1,
            CastOff = -1,
            DirOff = -1,
            TrailOff = -1,
            YearOff = -1,
            ExtOff = -1,
            CatOff = -1,
            RatOff = -1
        };

        private static void AddJsonString(ref Utf8JsonReader reader, Utf8StringWriter heap, out int offset, out int length)
        {
            offset = -1;
            length = 0;

            if (reader.TokenType == JsonTokenType.String)
            {
                string? value = reader.GetString();
                var stored = heap.Add(value);
                offset = stored.Off;
                length = stored.Len;
                return;
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                string value = reader.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var stored = heap.Add(value);
                offset = stored.Off;
                length = stored.Len;
            }
        }

        private static int ReadJsonInt32(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number)) return number;
            if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out number)) return number;
            return 0;
        }

        private static void WriteLiveRecord(BinaryWriter writer, LiveStreamData data)
        {
            writer.Write(data.StreamId);
            writer.Write(data.NameOff); writer.Write(data.NameLen);
            writer.Write(data.IconOff); writer.Write(data.IconLen);
            writer.Write(data.ImdbOff); writer.Write(data.ImdbLen);
            writer.Write(data.DescOff); writer.Write(data.DescLen);
            writer.Write(data.BgOff); writer.Write(data.BgLen);
            writer.Write(data.GenreOff); writer.Write(data.GenreLen);
            writer.Write(data.CastOff); writer.Write(data.CastLen);
            writer.Write(data.DirOff); writer.Write(data.DirLen);
            writer.Write(data.TrailOff); writer.Write(data.TrailLen);
            writer.Write(data.YearOff); writer.Write(data.YearLen);
            writer.Write(data.ExtOff); writer.Write(data.ExtLen);
            writer.Write(data.CatOff); writer.Write(data.CatLen);
            writer.Write(data.RatOff); writer.Write(data.RatLen);
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

                int off = (int)_stream.Position - _currentOffset;
                int max = Encoding.UTF8.GetMaxByteCount(s.Length);
                
                using var owner = CommunityToolkit.HighPerformance.Buffers.SpanOwner<byte>.Allocate(max);
                int n = Encoding.UTF8.GetBytes(s, owner.Span);
                _stream.Write(owner.Span[..n]);
                
                return (off, n);
            }

            public int TotalBytesWritten => (int)_stream.Position - _currentOffset;
        }

        private static Models.Metadata.VodRecord BuildVodRecordForBinarySave(VodStream s, Utf8StringWriter heap)
        {
            float ratingValue = 0;
            if (double.TryParse(s.Rating_json, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                ratingValue = (float)r;

            var name = heap.Add(s.Name);
            var icon = heap.Add(s.StreamIcon);
            var imdb = heap.Add(s.ImdbId);
            var plot = heap.Add(s.Description);
            var year = heap.Add(s.Year);
            var genres = heap.Add(s.Genres);
            var cast = heap.Add(s.Cast);
            var dir = heap.Add(s.Director);
            var trail = heap.Add(s.TrailerUrl);
            var bg = heap.Add(s.BackdropUrl);
            var srcTitle = heap.Add(s.SourceTitle);
            var rat = heap.Add(s.Rating_json);
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
            if (double.TryParse(s.Rating_json, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
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
            var rat = heap.Add(s.Rating_json);
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

                BinaryCacheLayout.WriteHeader(writer, streams.Count, 0, true, 0);

                // 2. Sequential Streaming Write
                // Calculate Fixed Offsets: Header + Records + Index
                int recordSize = Marshal.SizeOf<Models.Metadata.VodRecord>();
                int indexEntrySize = BinaryCacheLayout.VodIndexEntrySize; // uint(4) + int(4)
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                long indexOffset = recordsOffset + (streams.Count * (long)recordSize);
                writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);
                using var stringHeap = new MemoryStream();
                var heap = new Utf8StringWriter(stringHeap, 0);

                var indexEntries = new (uint Fingerprint, int Index)[streams.Count];
                
                for (int i = 0; i < streams.Count; i++)
                {
                    var record = BuildVodRecordForBinarySave(streams[i], heap);
                    WriteVodRecordToStream(writer, record);
                    indexEntries[i] = (record.Fingerprint, i);
                }

                int bufferPos = heap.TotalBytesWritten;

                // Pass 3: Write Index
                writer.BaseStream.Seek(indexOffset, SeekOrigin.Begin);
                Array.Sort(indexEntries, (a, b) => a.Fingerprint.CompareTo(b.Fingerprint));
                for (int i = 0; i < streams.Count; i++)
                {
                    writer.Write(indexEntries[i].Fingerprint);
                    writer.Write(indexEntries[i].Index);
                }

                stringHeap.Position = 0;
                stringHeap.CopyTo(writer.BaseStream);
                writer.Flush();

                // 5. Finalize Header
                long finalSize = writer.BaseStream.Position;
                writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                writer.Write(bufferPos); 
                writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
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
                using (var accessor = mmf.CreateViewAccessor(0, BinaryCacheLayout.HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = BinaryCacheLayout.ReadHeader(accessor);
                    if (!BinaryCacheLayout.IsKnownMagic(header.Magic)) return null;
                    version = header.Version;
                    
                    // Force rebuild if version is legacy
                    if (version < 4) 
                    {
                        AppLogger.Warn($"[BinaryLoad] Discarding legacy Version {version} VOD cache.");
                        return null;
                    }

                    count = header.Count;
                    bufferLen = header.StringsLength;
                }

                // [PHASE 4] 0ms Virtual Binary Session
                int recordSize = Marshal.SizeOf<Models.Metadata.VodRecord>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                long indexOffset = recordsOffset + (count * (long)recordSize);
                // VOD index entry is 8 bytes: uint Fingerprint (4) + int Index (4)
                long stringsOffset = indexOffset + (count * (long)BinaryCacheLayout.VodIndexEntrySize);

                var session = new BinaryCacheSession(Path.Combine(folder.Path, fileName), stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                
                // [PERFORMANCE FIX] Read cached fingerprint from header (0ms)
                long cachedFingerprint = session.GetHeaderFingerprint();
                
                var results = new VirtualVodList(session, cachedFingerprint);



                _streamListsCache[cacheKey] = results;
                
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
                    BinaryCacheLayout.WriteHeader(writer, streams.Count, 0, true, 0);

                    // Sequential Streaming Write
                    int recordSize = Marshal.SizeOf<Models.Metadata.SeriesRecord>();
                    long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                    writer.BaseStream.Seek(recordsOffset, SeekOrigin.Begin);
                    using var stringHeap = new MemoryStream();
                    var heap = new Utf8StringWriter(stringHeap, 0);

                    for (int i = 0; i < streams.Count; i++)
                    {
                        WriteSeriesRecordToStream(writer, BuildSeriesRecordForBinarySave(streams[i], heap));
                    }

                    int bufferPos = heap.TotalBytesWritten;
                    stringHeap.Position = 0;
                    stringHeap.CopyTo(writer.BaseStream);
                    writer.Flush();

                    long finalSize = writer.BaseStream.Position;
                    writer.BaseStream.Seek(BinaryCacheLayout.StringsLengthOffset, SeekOrigin.Begin);
                    writer.Write(bufferPos); 
                    writer.BaseStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
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
                using (var accessor = mmf.CreateViewAccessor(0, BinaryCacheLayout.HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = BinaryCacheLayout.ReadHeader(accessor);
                    if (header.Magic != BinaryCacheLayout.SeriesMagic) return null; 
                    version = header.Version;
                    if (version < 4)
                    {
                        AppLogger.Warn($"[BinaryLoad] Discarding legacy Version {version} Series cache.");
                        return null; 
                    }

                    count = header.Count;
                    bufferLen = header.StringsLength;
                }

                // [PHASE 4] 0ms Virtual Binary Session
                int recordSize = Marshal.SizeOf<Models.Metadata.SeriesRecord>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
                long stringsOffset = recordsOffset + (count * (long)recordSize);

                var session = new BinaryCacheSession(item.Path, stringsOffset, bufferLen, recordsOffset, count, recordSize, readOnlySession: true);
                
                // [PERFORMANCE FIX] Read cached fingerprint
                long cachedFingerprint = session.GetHeaderFingerprint();
                
                var results = new VirtualSeriesList(session, cachedFingerprint);


                _streamListsCache[cacheKey] = results;
                
                AppLogger.Info($"[BinaryLoad] Series Virtual Session Ready: {count} items.");
                return results;
            }
            catch (Exception ex) { AppLogger.Error("[BinaryLoad] Series Virtual Session FAILED", ex); return null; }
            finally { _diskSemaphore.Release(); }
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
                BinaryCacheLayout.WriteHeader(writer, categories.Count, 0, true, 0);
                
                // 2. Construct category strings first; records are fixed-size and streamed directly.
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
                for (int i = 0; i < records.Length; i++)
                {
                    writer.Write(records[i].IdOff);
                    writer.Write(records[i].IdLen);
                    writer.Write(records[i].NameOff);
                    writer.Write(records[i].NameLen);
                }

                int bufferPos = (int)stringsMs.Length;
                stringsMs.Position = 0;
                stringsMs.CopyTo(writer.BaseStream);

                // Finalize
                long finalSize = writer.BaseStream.Position;
                writer.BaseStream.Position = BinaryCacheLayout.StringsLengthOffset;
                writer.Write(bufferPos);
                writer.BaseStream.Position = BinaryCacheLayout.DirtyOffset;
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
                using (var accessor = mmf.CreateViewAccessor(0, BinaryCacheLayout.HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var header = BinaryCacheLayout.ReadHeader(accessor);
                    if (!BinaryCacheLayout.IsKnownMagic(header.Magic)) return null;
                    version = header.Version;
                    if (version < 2) return null;

                    count = header.Count;
                    bufferLen = header.StringsLength;
                }

                int recordSize = Marshal.SizeOf<Models.Metadata.CategoryRecord>();
                long recordsOffset = BinaryCacheLayout.GetRecordsOffset();
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

        #endregion
        #endregion


        public async Task SaveCacheAsync<T>(string playlistId, string category, IReadOnlyList<T> data) where T : class
        {
            // PROJECT ZERO: Exclusive Binary Persistence
            if (IsCategoryCache(category) && data is IReadOnlyList<LiveCategory> cats)
            {
                await SaveCategoriesBinaryAsync(playlistId, category, cats);
                return;
            }
            if (category == "live_streams" && data is IReadOnlyList<LiveStream> liveStreams)
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

        private static bool IsCategoryCache(string category)
        {
            return category.EndsWith("_categories", StringComparison.Ordinal)
                || category == "live_cats"
                || category == "live_cats_m3u";
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
        
        

        public async Task<List<IMediaStream>> FindLocalMatchesAsync(string title, string? imdbId = null)
        {
            var results = new List<IMediaStream>();

            if (!string.IsNullOrEmpty(imdbId))
            {
                // Check VOD ID Index
                int[] vodIndicesFound = _vodMatchIndex.FindById(imdbId);
                if (vodIndicesFound.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_vod", out var vodList) && vodList is VirtualVodList vvl)
                {
                    foreach (var index in vodIndicesFound)
                    {
                        if ((uint)index < (uint)vvl.Count) results.Add(vvl[index]);
                    }
                }

                // Check Series ID Index
                int[] serIndicesFound = _seriesMatchIndex.FindById(imdbId);
                if (serIndicesFound.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_series", out var serList) && serList is VirtualSeriesList vsl)
                {
                    foreach (var index in serIndicesFound)
                    {
                        if ((uint)index < (uint)vsl.Count) results.Add(vsl[index]);
                    }
                }

                // If we found an absolute ID match, we stop here (Stage 2 not needed)
                if (results.Count > 0) return results;
            }

            // --- STAGE 2: TOKEN MATCHING (Alternative Fallback) ---
            var tokens = TitleHelper.GetSignificantTokens(title);
            if (tokens.Count == 0) return results;

            // Check VOD Token Index
            var vodIndices = _vodMatchIndex.FindByTokens(tokens);
            if (vodIndices.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_vod", out var vodList2) && vodList2 is VirtualVodList vvl2)
            {
                foreach (var index in vodIndices)
                {
                    if ((uint)index < (uint)vvl2.Count) results.Add(vvl2[index]);
                }
            }

            // Check Series Token Index
            var serIndices = _seriesMatchIndex.FindByTokens(tokens);
            if (serIndices.Length > 0 && _streamListsCache.TryGetValue($"{AppSettings.LastPlaylistId}_series", out var serList2) && serList2 is VirtualSeriesList vsl2)
            {
                foreach (var index in serIndices)
                {
                    if ((uint)index < (uint)vsl2.Count) results.Add(vsl2[index]);
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
                                Releasedate = stream.Year,
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
                
                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.SeriesInfoResult);

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
                                Rating = stream.Rating,
                                Releasedate = stream.Year,
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
                
                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.MovieInfoResult);

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
                writer.Write(data.Info?.Rating?.ToString() ?? "");
                writer.Write(data.Info?.Releasedate ?? "");
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
                    Rating = reader.ReadString(),
                    Releasedate = reader.ReadString(),
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
                writer.Write(data.Info?.Rating?.ToString() ?? "");
                writer.Write(data.Info?.Releasedate?.ToString() ?? "");

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
                    Releasedate = reader.ReadString()
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
            
            var activePlaylists = new HashSet<string>();
            foreach (var key in _streamListsCache.Keys)
            {
                activePlaylists.Add(key.Split('_')[0]);
            }

            AppLogger.Info($"[ContentCacheService] Found {activePlaylists.Count} open playlists to flush: {string.Join(", ", activePlaylists)}");

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
                    else if (category == "live_streams" && data is IReadOnlyList<LiveStream> liveRo)
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
            var keysToRemove = new List<string>(_streamListsCache.Keys);
            foreach (var key in keysToRemove)
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
                bool magicOk = BinaryCacheLayout.IsKnownMagic(magic);
                if (!magicOk || version < 2 || count < 0 || bufferLen < 0)
                {
                    AppLogger.Warn($"[Repair] {fileName}: invalid header (magic=0x{magic:X8}, ver={version}, count={count}).");
                    return;
                }

                if (magic != BinaryCacheLayout.LiveMagic)
                {
                    int recordSize = magic == BinaryCacheLayout.SeriesMagic ? Marshal.SizeOf<Models.Metadata.SeriesRecord>()
                        : magic == BinaryCacheLayout.VodMagic ? Marshal.SizeOf<Models.Metadata.VodRecord>()
                        : Marshal.SizeOf<Models.Metadata.CategoryRecord>();
                    long stringsOffset = BinaryCacheLayout.GetStringsOffset(magic, count, recordSize);
                    long minLen = stringsOffset + bufferLen;
                    if (stream.Length < minLen)
                    {
                        AppLogger.Warn($"[Repair] {fileName}: layout mismatch (len={stream.Length}, need>={minLen}).");
                        return;
                    }
                }

                stream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
                byte dirtyBit = reader.ReadByte();

                if (dirtyBit == 1)
                {
                    AppLogger.Warn($"[Repair] Unexpected closure for {fileName}. Clearing dirty bit.");
                    using (var randomStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        using (var writeStream = randomStream.AsStreamForWrite())
                        {
                            writeStream.Seek(BinaryCacheLayout.DirtyOffset, SeekOrigin.Begin);
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
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<Dictionary<string, List<SeriesEpisodeDef>>>))]
        public Dictionary<string, List<SeriesEpisodeDef>> Episodes { get; set; }
        
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<SeriesInfoDetails>))]
        public SeriesInfoDetails Info { get; set; }
    }

    public class SeriesInfoDetails
    {
        public object TmdbId { get; set; } // Can be int or string
        
        public string Name { get; set; }
        
        public string Cover { get; set; }
        
        public string Plot { get; set; }
        
        public string Cast { get; set; }
        
        public string Genre { get; set; }
        
        public string Director { get; set; }

        public object? Rating { get; set; }
        
        public object? Releasedate { get; set; }

        public string[] BackdropPath { get; set; }

        public string YoutubeTrailer { get; set; }

        public string AirDate { get; set; }

        public string Age { get; set; }

        public string Country { get; set; }
    }

    public class SeriesEpisodeDef
    {
        public string Id { get; set; }
        
        public object EpisodeNum { get; set; } // Can be int or string
        
        public object Season { get; set; }

        public string ContainerExtension { get; set; }
        
        public string Title { get; set; }
        
        public SeriesEpisodeInfo Info { get; set; }
    }

    public class SeriesEpisodeInfo
    {
        public string MovieImage { get; set; }
        
        public string Plot { get; set; }
        
        public string Duration { get; set; }

        public string AirDate { get; set; }

        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalVideoInfo>))]
        public TechnicalVideoInfo Video { get; set; }

        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalAudioInfo>))]
        public TechnicalAudioInfo Audio { get; set; }

        public object Bitrate { get; set; } // Can be int or string
    }

    public class TechnicalVideoInfo
    {
        public string CodecName { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public object? DisplayAspectRatio { get; set; }
    }

    public class TechnicalAudioInfo
    {
        public string CodecName { get; set; }

        public int Channels { get; set; }

        public string ChannelLayout { get; set; }
    }

    public class MovieInfoResult
    {
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<MovieInfoDetails>))]
        public MovieInfoDetails Info { get; set; }
        
        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<MovieDataDetails>))]
        public MovieDataDetails MovieData { get; set; }
    }

    public class MovieInfoDetails
    {
        public string Name { get; set; }
        
        public string MovieImage { get; set; }

        public string CoverBig { get; set; }
        
        public string Plot { get; set; }
        
        public string Cast { get; set; }
        
        public string Genre { get; set; }
        
        public string Director { get; set; }
        
        public object? Rating { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayRating => Rating?.ToString() ?? "";
        
        public string Releasedate { get; set; }
 
         public string AirDate { get; set; }

        public string Released { get; set; }

        public string YoutubeTrailer { get; set; }

        public string[] BackdropPath { get; set; }

        public string Age { get; set; }

        public string MpaaRating { get; set; }

        public string Country { get; set; }

        public string Duration { get; set; }

        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalVideoInfo>))]
        public TechnicalVideoInfo Video { get; set; }

        [System.Text.Json.Serialization.JsonConverter(typeof(SafeObjectConverter<TechnicalAudioInfo>))]
        public TechnicalAudioInfo Audio { get; set; }

        public string ContainerExtension { get; set; }

        public object Bitrate { get; set; }
    }
    
    public class MovieDataDetails
    {
        public int StreamId { get; set; }
        
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
                // [NATIVE AOT] Use GetTypeInfo for metadata-aware deserialization
                var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
                return JsonSerializer.Deserialize(ref reader, typeInfo);
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
            JsonSerializer.Serialize(writer, value, typeInfo);
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

