using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Models.Tmdb;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Services.Metadata;

namespace ModernIPTVPlayer.Services.MediaInfo
{
    /// <summary>
    /// Manages the full page lifecycle: navigation → load → reveal → cleanup.
    /// Coordinates LoadPipeline, MediaInfoCommitService, and other services.
    /// </summary>
    internal sealed class PageOrchestrator : IDisposable
    {
        private readonly MediaInfoPage _page;
        private readonly LoadPipeline _loadPipeline;
        private readonly MediaInfoCommitService _commitService;
        private readonly VisualStateController _visualStateController;
        private readonly LayoutScheduler _layoutScheduler;
        private readonly EpisodesManager _episodesManager;
        private readonly CastDirectorManager _castDirectorManager;
        private readonly ParallaxController _parallaxController;
        private readonly TrailerManager _trailerManager;
        private readonly PlayerHandoffManager _playerHandoffManager;
        private bool _disposed;

        public PageOrchestrator(
            MediaInfoPage page,
            LoadPipeline loadPipeline,
            MediaInfoCommitService commitService,
            VisualStateController visualStateController,
            LayoutScheduler layoutScheduler,
            EpisodesManager episodesManager,
            CastDirectorManager castDirectorManager,
            ParallaxController parallaxController,
            TrailerManager trailerManager,
            PlayerHandoffManager playerHandoffManager)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _loadPipeline = loadPipeline ?? throw new ArgumentNullException(nameof(loadPipeline));
            _commitService = commitService ?? throw new ArgumentNullException(nameof(commitService));
            _visualStateController = visualStateController ?? throw new ArgumentNullException(nameof(visualStateController));
            _layoutScheduler = layoutScheduler ?? throw new ArgumentNullException(nameof(layoutScheduler));
            _episodesManager = episodesManager ?? throw new ArgumentNullException(nameof(episodesManager));
            _castDirectorManager = castDirectorManager ?? throw new ArgumentNullException(nameof(castDirectorManager));
            _parallaxController = parallaxController ?? throw new ArgumentNullException(nameof(parallaxController));
            _trailerManager = trailerManager ?? throw new ArgumentNullException(nameof(trailerManager));
            _playerHandoffManager = playerHandoffManager ?? throw new ArgumentNullException(nameof(playerHandoffManager));
            Debug.WriteLine("[ORCHESTRATOR] Initialized with all services");
        }

        public void OnNavigatedTo(NavigationEventArgs e, IMediaStream item, IMediaStream previousItem)
        {
            if (_disposed) return;
            Debug.WriteLine($"[ORCHESTRATOR] OnNavigatedTo: {item?.Title ?? "Unknown"} (mode: {e.NavigationMode})");
        }

        public async Task LoadDetailsAsync(IMediaStream item, UnifiedMetadata prePeekedMetadata = null, IMediaStream previousItem = null, int? loadSession = null)
        {
            if (_disposed || item == null) return;

            try
            {
                int session = _page.BeginLoadSession();
                Debug.WriteLine($"[ORCHESTRATOR] Session {session} started for: {item?.Title ?? "Unknown"}");

                _page.ResetPageState(resetBackground: false);
                _loadPipeline.Reset();
                _loadPipeline.TransitionTo(LoadPipeline.State.Preparing);
                _page.SetLoadStateInternal(PageLoadState.Loading);
                _loadPipeline.TransitionTo(LoadPipeline.State.Fetching);

                await _page.PrepareInfoSkeletonForRevealAsync();

                UnifiedMetadata metadata = prePeekedMetadata;
                if (metadata == null && item != null)
                {
                    try
                    {
                        var fetchSw = Stopwatch.StartNew();
                        metadata = await MetadataProvider.Instance.GetMetadataAsync(
                            item,
                            MetadataContext.Detail,
                            ct: _page.PageCts?.Token ?? default);
                        fetchSw.Stop();
                        Debug.WriteLine($"[ORCHESTRATOR] Metadata fetched in {fetchSw.ElapsedMilliseconds}ms: {metadata?.Title ?? "Unknown"}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ORCHESTRATOR] Metadata fetch failed: {ex.Message}");
                        _loadPipeline.SetError($"Metadata fetch: {ex.Message}");
                    }
                }

                if (metadata != null && _page.CurrentLoadingVersion == session)
                {
                    _page.UnifiedMetadata = metadata;
                    var commitSw = Stopwatch.StartNew();
                    _loadPipeline.TransitionTo(LoadPipeline.State.Committing);
                    await _commitService.CommitAsync(metadata, item);
                    commitSw.Stop();
                    Debug.WriteLine($"[ORCHESTRATOR] Commit completed in {commitSw.ElapsedMilliseconds}ms");

                    if (item is StremioMediaStream stremioSeries && stremioSeries.Meta?.Type == "series")
                    {
                        await _page.LoadSeriesDataAsync(metadata);
                    }

                    await _castDirectorManager.PopulateCastAndDirectorsAsync(metadata);

                    if (_page.CurrentLoadingVersion == session)
                    {
                        Debug.WriteLine("[ORCHESTRATOR] Triggering StaggeredRevealContent");
                        _page.StaggeredRevealContent();
                    }
                }
                else if (metadata != null)
                {
                    Debug.WriteLine($"[ORCHESTRATOR] Session mismatch: expected {session}, got {_page.CurrentLoadingVersion}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ORCHESTRATOR] LoadDetailsAsync error: {ex.Message}");
                _loadPipeline.SetError(ex.Message);
            }
        }

        public void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_disposed) return;
            Debug.WriteLine($"[ORCHESTRATOR] OnNavigatedFrom: {e.SourcePageType.Name} (mode: {e.NavigationMode})");

            _episodesManager?.Clear();
            _castDirectorManager?.Clear();
            _trailerManager?.CloseTrailerAsync().Wait(100);
            _playerHandoffManager?.CancelHandoff();
            _parallaxController?.Reset();
        }

        public void OnSizeChanged(double width, double height)
        {
            if (_disposed) return;
            _layoutScheduler.RequestLayout(LayoutRequestReason.SizeChanged);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Debug.WriteLine("[ORCHESTRATOR] Disposed");
        }
    }
}
