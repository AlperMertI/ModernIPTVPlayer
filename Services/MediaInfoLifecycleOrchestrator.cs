using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Services.Metadata;

namespace ModernIPTVPlayer.Services
{
    /// <summary>
    /// Orchestrates the lifecycle of a media loading session in the MediaInfoPage.
    /// Manages the state machine from navigation start to staggered reveal.
    /// </summary>
    public class MediaInfoLifecycleOrchestrator
    {
        private readonly IMediaInfoUIProxy _ui;
        private readonly ParallelRevealCoordinator _visuals;
        private readonly MediaInfoCommitService _commitService;
        
        private int _loadingVersion = 0;
        private CancellationTokenSource? _fetchCts;

        public MediaInfoLifecycleOrchestrator(
            IMediaInfoUIProxy ui, 
            ParallelRevealCoordinator visuals, 
            MediaInfoCommitService commitService)
        {
            _ui = ui;
            _visuals = visuals;
            _commitService = commitService;
        }

        /// <summary>
        /// Starts a new media load session.
        /// </summary>
        public async Task StartLoadSessionAsync(IMediaStream item, ModernIPTVPlayer.Models.Metadata.UnifiedMetadata? prePeeked = null, IMediaStream? previousItem = null)
        {
            // If called from a background thread, marshal the entry point to the UI thread
            if (!_ui.DispatcherQueue.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource();
                _ui.DispatcherQueue.TryEnqueue(async () =>
                {
                    await StartLoadSessionAsync(item, prePeeked, previousItem);
                    tcs.SetResult();
                });
                await tcs.Task;
                return;
            }

            // 1. Atomic Version Bump
            int sessionVersion = Interlocked.Increment(ref _loadingVersion);
            
            // 2. Visual Preparation (The Atomic Start)
            // This is synchronous and silent (XAML only).
            _ui.SetLoadState(PageLoadState.Loading);
            _visuals.Initialize();
            _visuals.PrepareForLoading();

            // 3. Logic Reset
            _commitService.Reset();
            _fetchCts?.Cancel();
            _fetchCts?.Dispose();
            _fetchCts = new CancellationTokenSource();
            var token = _fetchCts.Token;

            // 4. Cache Check (Flicker Prevention)
            var cached = prePeeked ?? MetadataProvider.Instance.TryPeekMetadata(item);
            bool isCached = cached != null;

            if (isCached)
            {
                // [CACHE HIT]
                bool committed = await _commitService.CommitAsync(cached, item, forceInitialCommit: true);
                if (IsSessionStale(sessionVersion)) return;

                if (committed)
                {
                    _ui.SetLoadState(PageLoadState.Revealing);
                    _ = _ui.IdentityControl?.DispatcherQueue.TryEnqueue(() => { _ = _visuals.StartRevealAsync(); });
                    _ui.SetLoadState(PageLoadState.Ready);
                }
            }
            else
            {
                // [CACHE MISS] - First Paint priming
                // We create a lightweight "Seed" metadata object from the initial item data
                // to show the background and title instantly while the network fetch is active.
                var seed = new UnifiedMetadata
                {
                    Title = item.Title,
                    PosterUrl = item.PosterUrl,
                    BackdropUrl = (item as Models.Stremio.StremioMediaStream)?.Meta?.Background ?? item.BackdropUrl,
                    MetadataSourceInfo = "Catalog Seed"
                };

                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] Cache Miss: Performing Seed Commit for {item.Title}");
                await _commitService.CommitAsync(seed, item, forceInitialCommit: true);
                
                _ui.MatchTitleSkeletonToContent(); // Prime the skeleton layout
                
                // [Senior] Reveal the skeletons immediately so the user doesn't see a blank page during fetch.
                _ui.SetLoadState(PageLoadState.Revealing);
                _ = _visuals.StartRevealAsync();
            }

            // 5. Fetch and Progressive Commit
            try
            {
                var fetched = await MetadataProvider.Instance.GetMetadataAsync(item, onUpdate: async (partial) => 
                {
                    if (IsSessionStale(sessionVersion)) return;
                    
                    // Always dispatch progressive commits and their subsequent UI state changes
                    _ui.DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (IsSessionStale(sessionVersion)) return;
                        bool changed = await _commitService.CommitAsync(partial, item);
                        
                        if (changed && !IsSessionStale(sessionVersion))
                        {
                            _ui.SetLoadState(PageLoadState.Revealing);
                            await _visuals.StartRevealAsync();
                        }
                    });
                }, ct: token);

                if (IsSessionStale(sessionVersion)) return;

                // Final commit marshaled to UI thread
                _ui.DispatcherQueue.TryEnqueue(async () =>
                {
                    if (IsSessionStale(sessionVersion)) return;
                    await _commitService.CommitAsync(fetched, item, forceInitialCommit: true);
                    
                    if (!IsSessionStale(sessionVersion))
                    {
                        _ui.SetLoadState(PageLoadState.Revealing);
                        await _visuals.StartRevealAsync();
                        _ui.SetLoadState(PageLoadState.Ready);
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-FLOW] Orchestrator: Fetch failed: {ex.Message}");
                _ui.SetLoadState(PageLoadState.Ready); // Ensure we don't get stuck in Loading
            }
        }

        public bool IsSessionStale(int version) => version != Volatile.Read(ref _loadingVersion);

        public void CancelCurrent()
        {
            Interlocked.Increment(ref _loadingVersion);
            _fetchCts?.Cancel();
        }
    }
}
